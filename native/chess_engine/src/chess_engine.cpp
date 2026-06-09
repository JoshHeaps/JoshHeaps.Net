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

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <memory>
#include <vector>


/* Search score constants. Scores are side-to-move-relative (negamax): positive is
 * good for whoever is to move. MATE_BOUND is the threshold above which a score is a
 * "mate in N" rather than a positional eval; INF is the window sentinel (kept above
 * MATE so negating it can never hit signed-overflow UB the way INT_MIN would). */
static constexpr int MATE       = 200000;
static constexpr int MATE_BOUND = MATE - 1000;
static constexpr int INF        = 1000000;

/* Bound kind stored in a TT entry. LOWER = a fail-high (true score >= stored),
 * UPPER = a fail-low (true score <= stored), EXACT = fully resolved. */
enum class Bound : uint8_t { NONE, EXACT, LOWER, UPPER };

/* One shared, process-wide transposition table backs every game (every engine
 * handle), so analysis persists and is reused across games. It is lock-free: each
 * slot is two 64-bit words — `data` (the packed payload) and `xorKey` (the Zobrist
 * key XOR-ed with `data`). A reader recovers the key as `xorKey ^ data`; if two
 * concurrent searches tore the pair, the recovered key won't match and the read is
 * treated as a miss — never a wrong-but-trusted entry (Hyatt's lockless hashing). */
struct TTEntry {
    std::atomic<uint64_t> xorKey{0};
    std::atomic<uint64_t> data{0};
};

struct TranspositionTable {
    std::unique_ptr<TTEntry[]> entries;
    size_t mask = 0;                             /* count - 1; count is a power of two */
};

static TranspositionTable g_tt;
static constexpr size_t   TT_MEGABYTES = 256;

/* Pack/unpack the 64-bit payload: score(32) | move(16) | depth(8) | bound(8). A stored
 * entry always has depth >= 1 and a non-NONE bound, so a real entry never packs to 0 —
 * letting data == 0 mean "empty slot". */
static uint64_t    tt_pack(int score, chess::Move move, int depth, Bound bound) {
    return  static_cast<uint64_t>(static_cast<uint32_t>(score))
         | (static_cast<uint64_t>(move.data)                  << 32)
         | (static_cast<uint64_t>(static_cast<uint8_t>(depth)) << 48)
         | (static_cast<uint64_t>(static_cast<uint8_t>(bound)) << 56);
}
static int         tt_score(uint64_t d) { return static_cast<int32_t>(static_cast<uint32_t>(d & 0xFFFFFFFFu)); }
static chess::Move tt_move (uint64_t d) { return chess::Move(static_cast<uint16_t>(d >> 32)); }
static int         tt_depth(uint64_t d) { return static_cast<int>(static_cast<uint8_t>(d >> 48)); }
static Bound       tt_bound(uint64_t d) { return static_cast<Bound>(static_cast<uint8_t>(d >> 56)); }

/* Internal engine state. One ChessEngine = one game. The table is NOT here: it is the
 * shared g_tt above. */
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

/* Maps the 1..20 difficulty to a search depth. Kept modest: the search has no
 * quiescence yet, so deep fixed-depth runs get expensive quickly. */
static int depth_for_skill(int skill) {
    return skill;    /* skill N -> N plies */
}

static size_t floor_pow2(size_t n) {
    size_t p = 1;
    while ((p << 1) != 0 && (p << 1) <= n) p <<= 1;
    return p;
}

/* Allocate the shared table exactly once, to the largest power-of-two entry count that
 * fits in TT_MEGABYTES. Power-of-two count lets indexing use `key & mask`. Thread-safe:
 * call_once guards the first concurrent engine_create. Entries start zeroed (empty). */
static void ensure_tt() {
    static std::once_flag once;
    std::call_once(once, [] {
        size_t count = floor_pow2((TT_MEGABYTES << 20) / sizeof(TTEntry));
        if (count < 1) count = 1;
        g_tt.entries = std::make_unique<TTEntry[]>(count);
        g_tt.mask    = count - 1;
    });
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

/* evaluate() is white-positive (absolute). Negamax needs it relative to the side to
 * move, so flip the sign when black is to move. */
static int evaluate_stm(const chess::Position& pos, bool whiteToMove) {
    int s = evaluate(pos);
    return whiteToMove ? s : -s;
}

/* Mate scores are "mate in N from THIS node", so they must be re-anchored to the
 * probing node's ply when crossing the TT (store adds ply, retrieve subtracts it).
 * Non-mate scores pass through untouched. */
static int score_to_tt(int s, int ply)   { return s >=  MATE_BOUND ? s + ply : s <= -MATE_BOUND ? s - ply : s; }
static int score_from_tt(int s, int ply) { return s >=  MATE_BOUND ? s - ply : s <= -MATE_BOUND ? s + ply : s; }

static int piece_value(chess::PieceType pt) {
    switch (pt) {
        case chess::PAWN:   return 100;
        case chess::KNIGHT: return 320;
        case chess::BISHOP: return 330;
        case chess::ROOK:   return 500;
        case chess::QUEEN:  return 900;
        default:            return 0;
    }
}

/* Heuristic for searching the most promising moves first, which makes alpha-beta
 * prune far more. The TT's best move (if any) goes first, then checks, then captures
 * by MVV-LVA (grab the most valuable victim with the least valuable attacker).
 * `scoreChecks` gates the expensive gives_check term to near-leaf nodes. */
static int order_score(chess::Position& pos, chess::Move m, chess::Move ttMove, bool scoreChecks) {
    if (m == ttMove)
        return 2000000;                  /* dwarfs any capture/check score below */

    int score = 0;

    if (scoreChecks && pos.gives_check(m))
        score += 1000;

    chess::Piece victim = pos.piece_on(m.to());
    if (victim != chess::NO_PIECE)
        score += 100 + 10 * piece_value(chess::type_of(victim))
                     - piece_value(chess::type_of(pos.piece_on(m.from())));
    else if (m.type() == chess::EN_PASSANT)
        score += 100 + 10 * piece_value(chess::PAWN);

    return score;
}

/* Sort the move list in place, best-scoring first. Scores are computed once up
 * front so gives_check isn't re-evaluated on every comparison. ttMove may be
 * MOVE_NONE, in which case no move matches it and ordering falls back to captures. */
static void order_moves(chess::Position& pos, chess::MoveList& moves, chess::Move ttMove, bool scoreChecks) {
    struct ScoredMove { int score; chess::Move move; };
    ScoredMove scored[256];

    for (int i = 0; i < moves.size(); i++)
        scored[i] = { order_score(pos, moves.moves[i], ttMove, scoreChecks), moves.moves[i] };

    std::sort(scored, scored + moves.size(),
              [](const ScoredMove& a, const ScoredMove& b) { return a.score > b.score; });

    for (int i = 0; i < moves.size(); i++)
        moves.moves[i] = scored[i].move;
}

extern "C" {

CHESS_API EngineHandle CHESS_CALL engine_create(const char* options) {
    ensure_initialized();
    ensure_tt();
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

/* Negamax alpha-beta over the shared transposition table. `maxDepth` is the searching
 * bot's difficulty (its root depth); `depth` is remaining depth (draft); `ply` is
 * distance from the root (mate scoring only). Scores are side-to-move-relative.
 * Fail-soft: returns the true best found even outside [alpha, beta]. */
static int negamax(chess::Position& pos, int maxDepth, int depth, int ply,
                   int alpha, int beta, bool whiteToMove, uint64_t& nodes) {
    nodes++;

    /* A draw is 0 even at the search horizon, and the TT key doesn't encode repetition
     * history, so this must come before both the leaf eval and any TT probe. */
    if (ply > 0 && pos.is_draw())
        return 0;

    if (depth <= 0)
        return evaluate_stm(pos, whiteToMove);

    const uint64_t key  = pos.key();
    TTEntry&       slot = g_tt.entries[key & g_tt.mask];
    const uint64_t data = slot.data.load(std::memory_order_relaxed);
    const uint64_t xkey = slot.xorKey.load(std::memory_order_relaxed);

    chess::Move ttMove = chess::MOVE_NONE;

    if (data != 0 && (xkey ^ data) == key) {          /* lockless: XOR check rejects torn reads */
        ttMove = tt_move(data);                       /* always reusable for ordering */
        int   edepth = tt_depth(data);
        Bound b      = tt_bound(data);

        /* Trust the score only if it was searched deep enough for this node AND no deeper
         * than this bot's own strength — so a weak bot can't borrow a stronger game's
         * deeper analysis (it still gets the move for ordering, which can't leak strength). */
        if (edepth >= depth && edepth <= maxDepth) {
            int s = score_from_tt(tt_score(data), ply);
            if (b == Bound::EXACT)               return s;
            if (b == Bound::LOWER && s >= beta)  return s;
            if (b == Bound::UPPER && s <= alpha) return s;
        }
    }

    chess::MoveList moves;
    pos.generate_legal(moves);

    if (moves.size() == 0)
        return pos.is_draw() ? 0 : -MATE + ply;       /* checkmate against side to move */

    order_moves(pos, moves, ttMove, depth <= 2);

    const int alphaOrig = alpha;
    int best = -INF;
    chess::Move bestMove = chess::MOVE_NONE;

    for (int i = 0; i < moves.size(); i++) {
        chess::Move move = moves.moves[i];
        pos.do_move(move);
        int score = -negamax(pos, maxDepth, depth - 1, ply + 1, -beta, -alpha, !whiteToMove, nodes);
        pos.undo_move(move);

        if (score > best) {
            best = score;
            bestMove = move;
        }
        if (best > alpha)
            alpha = best;
        if (best >= beta)
            break;                                    /* fail-high cutoff */
    }

    Bound flag = best <= alphaOrig ? Bound::UPPER
               : best >= beta      ? Bound::LOWER
               :                     Bound::EXACT;

    /* Depth-preferred replacement: keep the deepest analysis of each slot. The stored
     * payload is written before the xorKey so any concurrent reader that catches a
     * half-update fails the XOR check and treats it as a miss. */
    int storedDepth = (data == 0) ? -1 : tt_depth(data);
    if (depth >= storedDepth) {
        uint64_t packed = tt_pack(score_to_tt(best, ply), bestMove, depth, flag);
        slot.data.store(packed, std::memory_order_relaxed);
        slot.xorKey.store(key ^ packed, std::memory_order_relaxed);
    }

    return best;
}

CHESS_API int CHESS_CALL engine_best_move(EngineHandle engine,
                                          const char* fen,
                                          const char* history,
                                          char* out_buf,
                                          int   out_len) {
    if (!engine)       return CHESS_ERR_NULL_HANDLE;
    if (!fen || !*fen) return CHESS_ERR_BAD_FEN;

    auto held = std::make_unique<chess::Position>(chess::Position::from_fen(fen));
    chess::Position& pos = *held;
    bool whiteToMove = pos.side_to_move() == chess::WHITE;

    /* Seed the prior positions (one FEN per line) so is_draw() sees repetitions and
     * the 50-move count that the current FEN alone can't express. */
    if (history && *history) {
        std::vector<uint64_t> priorKeys;
        const char* p = history;
        while (*p) {
            const char* nl = std::strchr(p, '\n');
            size_t len = nl ? static_cast<size_t>(nl - p) : std::strlen(p);
            if (len > 0)
                priorKeys.push_back(chess::Position::from_fen(std::string(p, len)).key());
            if (!nl) break;
            p = nl + 1;
        }
        if (!priorKeys.empty())
            pos.seed_history(priorKeys.data(), static_cast<int>(priorKeys.size()));
    }

    chess::MoveList moves;
    pos.generate_legal(moves);
    if (moves.size() == 0)
        return CHESS_ERR_NO_MOVE;

    uint64_t nodes = 0;
    int maxDepth = depth_for_skill(engine->skill);
    chess::Move bestMove = moves.moves[0];            /* guaranteed-legal fallback */

    /* Iterative deepening: each depth seeds the next depth's move ordering (via the
     * previous best move and the TT it filled), which makes the deeper search prune
     * far harder than searching to maxDepth cold. */
    for (int d = 1; d <= maxDepth; d++) {
        int alpha = -INF, beta = INF;
        chess::Move iterBest = bestMove;
        int iterScore = -INF;

        order_moves(pos, moves, iterBest, true);

        for (int i = 0; i < moves.size(); i++) {
            chess::Move move = moves.moves[i];
            pos.do_move(move);
            int score = -negamax(pos, maxDepth, d - 1, 1, -beta, -alpha, !whiteToMove, nodes);
            pos.undo_move(move);

            if (score > iterScore) {
                iterScore = score;
                iterBest = move;
            }
            if (score > alpha)
                alpha = score;
        }

        bestMove = iterBest;                          /* commit only a fully completed iteration */

        std::fprintf(stderr, "depth %d nodes %llu best %s score %d\n",
                     d, static_cast<unsigned long long>(nodes),
                     chess::move_to_uci(iterBest).c_str(), iterScore);
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
