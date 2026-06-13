/* eval.cpp - classic and learned position evaluation, plus feature computation.
 * See eval.h for the public surface. Everything else here is file-static. */
#include "eval.h"
#include "bitboard.h"
#include "position.h"

#include <algorithm>
#include <cmath>

/* ---- Shared piece values ------------------------------------------------------------- */

int piece_value(chess::PieceType pt) {
    switch (pt) {
    case chess::PAWN:   return 100;
    case chess::KNIGHT: return 320;
    case chess::BISHOP: return 330;
    case chess::ROOK:   return 500;
    case chess::QUEEN:  return 900;
    default:            return 0;
    }
}

/* ---- Classic (hand-crafted) evaluation ----------------------------------------------- */

/* Positional bonus (centipawns) from a square's Chebyshev distance to the center, added to
 * a piece's score by evaluatePiece. Returns 20 (dead center) .. 140 (edge / corner). */
static int center_multiplier(chess::Square s) {
    /* |2*coord - 7| is the distance from center in half-squares: 1 (center) .. 7 (edge). */
    int fileDist = std::abs(2 * int(chess::file_of(s)) - 7);
    int rankDist = std::abs(2 * int(chess::rank_of(s)) - 7);
    int dist = fileDist > rankDist ? fileDist : rankDist;    /* Chebyshev distance, 1 .. 7 */

    return (8-dist) * 20;
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
        score += (6 - squaresToPromotion) * 100; // Bonus for passed pawns, more as they get closer to promotion
    if (isDoubled)
        score -= 20; // Penalty for doubled pawns
    if (isBlocked)
        score -= 20; // Penalty for blocked pawns

    return score;
}

static int castleIncentive(const chess::Position& pos, chess::Color c) {
    chess::Bitboard pcs = pos.pieces();
    int total = 0;
    while (pcs) {
        chess::Square s = chess::pop_lsb(pcs);
        chess::Piece pc = pos.piece_on(s);
        chess::Color c = chess::color_of(pc);
        total += piece_value(chess::type_of(pc));
    }

    chess::Square k = pos.king_square(c);
    bool castled = (c == chess::WHITE) ? (k == chess::G1 || k == chess::C1)
        : (k == chess::G8 || k == chess::C8);

    return castled ? (total / 10) : 0;
}

static int evaluatePiece(const chess::Position& pos, const chess::Square& s, const chess::Piece& pc, const chess::Color& c) {
    int score = 0;
    switch (chess::type_of(pc)) {
        case chess::PAWN:   score = evaluatePawn(pos, c, s); break;
        case chess::KNIGHT: score = 320; break;
        case chess::BISHOP: score = 330; break;
        case chess::ROOK:   score = 500; break;
        case chess::QUEEN:  score = 900; break;
        case chess::KING:   score = castleIncentive(pos, c); break;
        default: return 0;
    }

    score += center_multiplier(s);

    if (pc != chess::B_PAWN && pc != chess::W_PAWN)
        score += piece_mobility(pos, s, pc, c) * 25;

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

/* ---- Learned (phase-split tables + feature knobs) evaluation ---------------------------
 * The model is a linear combination of features whose weights are learned from outcomes:
 *   eval = Σ pieces [ material + blend(mg, eg, phase) ]  +  Σ features featW[i]·activation[i]
 * compute_features() is the single source of feature activations, used by BOTH the eval here
 * and the trainer, so the two can never disagree. */

double game_phase(const chess::Position& pos) {
    int npm = chess::popcount(pos.pieces(chess::KNIGHT)) * 1
            + chess::popcount(pos.pieces(chess::BISHOP)) * 1
            + chess::popcount(pos.pieces(chess::ROOK))   * 2
            + chess::popcount(pos.pieces(chess::QUEEN))  * 4;
    constexpr int MAX = 24;
    if (npm >= MAX) return 0.0;
    return double(MAX - npm) / MAX;
}

/* Blend a midgame and endgame value by phase, rounding per-piece (so training credits a
 * square the same way the eval reads it). */
static int blend(int mg, int eg, double phase) {
    return int(std::lround((1.0 - phase) * mg + phase * eg));
}

void compute_features(chess::Position& pos, chess::Color c, double phase, double out[FEATURE_NB]) {
    for (int i = 0; i < FEATURE_NB; ++i) out[i] = 0.0;

    /* Mobility: legal moves for color c, bucketed by the moving piece's type. */
    chess::MoveList moves;
    pos.generate_legal_for(c, moves);
    for (int i = 0; i < moves.size(); ++i) {
        switch (chess::type_of(pos.piece_on(moves.moves[i].from()))) {
            case chess::KNIGHT: out[FEAT_MOB_N] += 1; break;
            case chess::BISHOP: out[FEAT_MOB_B] += 1; break;
            case chess::ROOK:   out[FEAT_MOB_R] += 1; break;
            case chess::QUEEN:  out[FEAT_MOB_Q] += 1; break;
            default: break;
        }
    }

    /* Pawn structure. */
    chess::Bitboard pawns = pos.pieces(c, chess::PAWN);
    chess::Bitboard bb = pawns;
    while (bb) {
        chess::Square s = chess::pop_lsb(bb);

        if (!(front_span(c, s) & pos.pieces(~c, chess::PAWN))) {          /* passed */
            chess::Rank r = chess::rank_of(s);
            int toPromotion = (c == chess::WHITE) ? (chess::RANK_8 - r) : (r - chess::RANK_1);
            out[FEAT_PASSED] += (6 - toPromotion) * phase;               /* 0..5 ranks advanced, late-game */
        }
    }

    /* Pawn links: friendly pawns that are defended by another friendly pawn (one per
     * defended pawn, regardless of how many defenders). */
    chess::Bitboard pawnAttacks = 0;
    chess::Bitboard pp = pawns;
    while (pp) pawnAttacks |= chess::PawnAttacks[c][chess::pop_lsb(pp)];
    out[FEAT_PAWN_LINK] += chess::popcount(pawns & pawnAttacks);

    /* King safety: friendly pawns sheltering the king (its file + adjacent files, the two
     * ranks in front), worth more in the midgame. */
    chess::Square k = pos.king_square(c);
    chess::File kf = chess::file_of(k);
    chess::Rank kr = chess::rank_of(k);
    chess::Bitboard kingFiles = chess::file_bb(kf);
    if (kf > chess::FILE_A) kingFiles |= chess::file_bb(chess::File(kf - 1));
    if (kf < chess::FILE_H) kingFiles |= chess::file_bb(chess::File(kf + 1));
    chess::Bitboard shelterRanks = 0;
    for (int d = 1; d <= 2; ++d) {
        int rr = (c == chess::WHITE) ? (kr + d) : (kr - d);
        if (rr >= 0 && rr <= 7) shelterRanks |= (0xFFULL << (8 * rr));
    }
    out[FEAT_KING] += chess::popcount(kingFiles & shelterRanks & pawns) * (1.0 - phase);
}

/* Learned eval (white-positive/absolute, like evaluate()): material + phase-blended piece-
 * square tables + learned feature weights. Black pieces index the rank-mirrored square
 * (s ^ 56) so both colors share one white-relative table. Non-const because mobility
 * generates legal moves (which the position's move generator does via do/undo). */
static int evaluateLearned(chess::Position& pos, const EvalParams& ep) {
    double phase = game_phase(pos);
    int score = 0;

    chess::Bitboard white = pos.pieces(chess::WHITE);
    while (white) {
        chess::Square s = chess::pop_lsb(white);
        chess::PieceType pt = chess::type_of(pos.piece_on(s));
        score += piece_value(pt) + blend(ep.mg[pt][s], ep.eg[pt][s], phase);
    }

    chess::Bitboard black = pos.pieces(chess::BLACK);
    while (black) {
        chess::Square s = chess::pop_lsb(black);
        chess::PieceType pt = chess::type_of(pos.piece_on(s));
        score -= piece_value(pt) + blend(ep.mg[pt][s ^ 56], ep.eg[pt][s ^ 56], phase);
    }

    double wFeat[FEATURE_NB], bFeat[FEATURE_NB];
    compute_features(pos, chess::WHITE, phase, wFeat);
    compute_features(pos, chess::BLACK, phase, bFeat);

    double feature = 0.0;
    for (int i = 0; i < FEATURE_NB; ++i)
        feature += ep.featW[i] * (wFeat[i] - bFeat[i]) / FEAT_SCALE[i];
    score += int(std::lround(feature));

    return score;
}

/* evaluate() is white-positive (absolute). Negamax needs it relative to the side to
 * move, so flip the sign when black is to move. */
int evaluate_stm(chess::Position& pos, bool whiteToMove, const EvalParams& ep) {
    int s = (ep.variant == EVAL_LEARNED) ? evaluateLearned(pos, ep) : evaluate(pos);
    return whiteToMove ? s : -s;
}
