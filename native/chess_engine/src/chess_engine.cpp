/* chess_engine.cpp - the DLL boundary (extern "C" ABI).
 *
 * The rules layer (board, move generation, make/unmake, hashing, perft) lives in
 * the other src/*.cpp files and is ready to use. engine_best_move is intentionally
 * left for YOU: that is where your search/evaluation goes. Everything below the
 * FEN-in / UCI-out boundary should stay native — the managed side crosses it once
 * per move.
 */
#ifndef CHESS_ENGINE_BUILD
#define CHESS_ENGINE_BUILD            /* fallback when not building via CMake (which defines it) */
#endif
#pragma once

#include "chess_engine.h"
#include "bitboard.h"
#include "zobrist.h"
#include "position.h"
#include "movegen.h"
#include "uci.h"

#include <cstdlib>
#include <cstring>
#include <limits>
#include <new>
#include <string>
#include <memory>


/* Internal engine state. Put your search tables, transposition table, etc. here. */
struct ChessEngine {
    int skill = 20;          /* 1..20 from the UI; controls search depth */
};

static int copy_out(const char* src, char* out_buf, int out_len) {
    if (!out_buf || out_len <= 0) return CHESS_ERR_BUFFER;
    const size_t need = std::strlen(src) + 1;            /* + NUL */
    if (need > static_cast<size_t>(out_len)) return CHESS_ERR_BUFFER;
    std::memcpy(out_buf, src, need);
    return CHESS_OK;
}

/* Attack tables and Zobrist keys are global and read-only after this runs. */
static void ensure_initialized() {
    static bool done = false;
    if (done) return;
    chess::init_bitboards();
    chess::Zobrist::init();
    done = true;
}

/* Pulls "skill=N" out of the engine_create options string; clamps to the UI's 1..20. */
static int parse_skill(const char* options, int fallback) {
    if (!options) return fallback;
    const char* p = std::strstr(options, "skill=");
    if (!p) return fallback;
    int v = std::atoi(p + 6);
    return v < 1 ? 1 : v > 20 ? 20 : v;
}

/* Maps the 1..20 difficulty to a search depth. Kept modest: the search has no move
 * ordering or quiescence yet, so deep fixed-depth runs get expensive quickly. */
static int depth_for_skill(int skill) {
    return skill;    /* skill 1 -> 2 plies ... skill 20 -> 7 plies */
}

/* Positional multiplier in [0.5, 2.0] based on a square's distance from the four
 * center squares (d4/e4/d5/e5): 2.0 dead center, 0.5 in a corner, scaling linearly.
 * Multiply a piece's base value by this to reward central placement. */
static double center_multiplier(chess::Square s) {
    /* |2*coord - 7| is the distance from center in half-squares: 1 (center) .. 7 (edge). */
    int fileDist = std::abs(2 * int(chess::file_of(s)) - 7);
    int rankDist = std::abs(2 * int(chess::rank_of(s)) - 7);
    int dist = fileDist > rankDist ? fileDist : rankDist;    /* Chebyshev distance, 1 .. 7 */

    return dist * 20;                     /* 1 -> 2.0, 7 -> 0.5 */
}

static int piece_mobility(const chess::Position& pos, chess::Square s, chess::Piece pc, chess::Color c) {
    chess::Bitboard occ = pos.pieces();
    chess::Bitboard targets;

    switch (chess::type_of(pc)) {
        case chess::KNIGHT: targets = chess::KnightAttacks[s];        break;
        case chess::BISHOP: targets = chess::bishop_attacks(s, occ);  break;
        case chess::ROOK:   targets = chess::rook_attacks(s, occ);    break;
        case chess::QUEEN:  targets = chess::queen_attacks(s, occ);   break;
        case chess::KING:   targets = chess::KingAttacks[s];          break;
        default:            return 0;   // pawns: mobility usually handled via push/attack separately
    }

    return chess::popcount(targets & ~pos.pieces(c));   // exclude squares blocked by own pieces
}

static chess::Bitboard front_span(chess::Color c, chess::Square s) {
    chess::File f = file_of(s);
    chess::Bitboard files = file_bb(f);
    if (f > chess::FILE_A) files |= chess::file_bb(chess::File(f - 1));
    if (f < chess::FILE_H) files |= chess::file_bb(chess::File(f + 1));

    // Pawns never sit on rank 1 or 8, so rank is 1..6 and these shifts
    // are always in [8,56] — no shift-by-64 UB to guard against.
    chess::Rank r = rank_of(s);
    chess::Bitboard ahead = (c == chess::WHITE) ? (~0ULL << (8 * (r + 1)))   // ranks > r
        : ((1ULL << (8 * r)) - 1);   // ranks < r
    return files & ahead;
}

static chess::Bitboard front_span_file_only(chess::Color c, chess::Square s) {
    chess::File f = file_of(s);
    chess::Bitboard files = file_bb(f);

    // Pawns never sit on rank 1 or 8, so rank is 1..6 and these shifts
    // are always in [8,56] — no shift-by-64 UB to guard against.
    chess::Rank r = rank_of(s);
    chess::Bitboard ahead = (c == chess::WHITE) ? (~0ULL << (8 * (r + 1)))   // ranks > r
        : ((1ULL << (8 * r)) - 1);   // ranks < r
    return files & ahead;
}

static int evaluatePawn(const chess::Position& pos, const chess::Color c, const chess::Square s) {
    chess::Bitboard span = front_span(c, s);
    chess::Bitboard file_span = front_span_file_only(c, s);
    chess::Rank r = rank_of(s);
    int squaresToPromotion = (c == chess::WHITE) ? (chess::RANK_8 - r) : (r - chess::RANK_1);;
    bool isPassed = !(span & pos.pieces(~c, chess::PAWN));
    bool isBlocked = (file_span & pos.pieces(c, chess::PAWN)) | (file_span & pos.pieces(~c, chess::PAWN));
    bool isDoubled = (file_span & pos.pieces(c, chess::PAWN));

    int score = 100;

    if (isPassed && !isBlocked)
        score += squaresToPromotion * 10; // Bonus for passed pawns, more as they get closer to promotion
    if (isDoubled)
        score -= 20; // Penalty for doubled pawns
    if (isBlocked)
        score -= 20; // Penalty for blocked pawns

    return score;
}

static int evaluatePiece(const chess::Position& pos, const chess::Square& s, const chess::Piece& pc, const chess::Color& c) {
    int score = 0;
    switch (chess::type_of(pc)) {
        case chess::PAWN:   score = evaluatePawn(pos, c, s); break;
        case chess::KNIGHT: score = 320; break;
        case chess::BISHOP: score = 330; break;
        case chess::ROOK:   score = 500; break;
        case chess::QUEEN:  score = 900; break;
        default: return 0;
    }

    score += center_multiplier(s);
    score += piece_mobility(pos, s, pc, c) * 10;

    return score;
}

static int evaluate(const chess::Position& pos) {
    int score = 0;
    chess::Bitboard white = pos.pieces(chess::WHITE);

    while (white) {
		chess::Square s = chess::pop_lsb(white);
		chess::Piece pc = pos.piece_on(s);
		chess::Color c = chess::color_of(pc);
        score += evaluatePiece(pos, s, pc, c);
    }

    chess::Bitboard black = pos.pieces(chess::BLACK);

    while (black) {
        chess::Square s = chess::pop_lsb(black);
        chess::Piece pc = pos.piece_on(s);
        chess::Color c = chess::color_of(pc);
        score -= evaluatePiece(pos, s, pc, c);
    }

    return score;
}

extern "C" {

CHESS_API EngineHandle CHESS_CALL engine_create(const char* options) {
    ensure_initialized();
    auto* e = new (std::nothrow) ChessEngine();
    if (!e) return nullptr;
    e->skill = parse_skill(options, e->skill);
    return e;
}

CHESS_API int CHESS_CALL engine_set_option(EngineHandle engine,
                                           const char* /*name*/,
                                           const char* /*value*/) {
    if (!engine) return CHESS_ERR_NULL_HANDLE;
    return CHESS_OK;                                      /* TODO: store options */
}

static int alpha_beta(chess::Position& pos, int depth, int maxDepth, int bestForWhite, int bestForBlack, bool whiteToMove) {
	if (depth == maxDepth)
		return evaluate(pos);

	chess::MoveList moves;
    pos.generate_legal(moves);

    if (moves.size() == 0)
        return pos.is_draw() ? 0 : whiteToMove ? -200000 + depth : 200000 - depth;

    for (int i = 0; i < moves.size(); i++) {
		chess::Move move = moves.moves[i];
		pos.do_move(move);
		int moveScore = alpha_beta(pos, depth + 1, maxDepth, bestForWhite, bestForBlack, !whiteToMove);
        if (whiteToMove) {
            if (moveScore >= bestForBlack) {
				pos.undo_move(move);
                return bestForBlack;
            }
            if (moveScore > bestForWhite)
				bestForWhite = moveScore;
        }
        else {
            if (moveScore <= bestForWhite) {
				pos.undo_move(move);
                return bestForWhite;
            }
            if (moveScore < bestForBlack)
				bestForBlack = moveScore;
        }

        pos.undo_move(move);
    }

	return whiteToMove ? bestForWhite : bestForBlack;
}

CHESS_API int CHESS_CALL engine_best_move(EngineHandle engine,
                                          const char* fen,
                                          char* out_buf,
                                          int   out_len) {
    if (!engine)       return CHESS_ERR_NULL_HANDLE;
    if (!fen || !*fen) return CHESS_ERR_BAD_FEN;

    auto held = std::make_unique<chess::Position>(chess::Position::from_fen(fen));
    chess::Position& pos = *held;
    bool whiteToMove = pos.side_to_move() == chess::WHITE;
    chess::MoveList moves;
    pos.generate_legal(moves);
    if (moves.size() == 0)
        return CHESS_ERR_NO_MOVE;

    int maxDepth = depth_for_skill(engine->skill);

	int bestForWhite = std::numeric_limits<int>::min();
	int bestForBlack = std::numeric_limits<int>::max();
	chess::Move bestMove = moves.moves[0];

    for (int i = 0; i < moves.size(); i++) {
		chess::Move move = moves.moves[i];
		pos.do_move(move);
        int score = alpha_beta(pos, 1, maxDepth, bestForWhite, bestForBlack, !whiteToMove);
        pos.undo_move(move);

        if (whiteToMove && score > bestForWhite) {
			bestForWhite = score;
            bestMove = move;
        }
        else if (!whiteToMove && score < bestForBlack) {
			bestForBlack = score;
            bestMove = move;
        }
    }

    return copy_out(chess::move_to_uci(bestMove).c_str(), out_buf, out_len);
}

CHESS_API int CHESS_CALL engine_version(char* out_buf, int out_len) {
    return copy_out("custom-engine 0.1.0", out_buf, out_len);
}

CHESS_API void CHESS_CALL engine_destroy(EngineHandle engine) {
    delete engine;                                        /* delete nullptr is safe */
}

} /* extern "C" */
