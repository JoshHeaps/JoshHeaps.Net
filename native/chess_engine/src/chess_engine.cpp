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
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
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

/* Eval variant for an engine handle. CLASSIC = the hand-crafted evaluate(); LEARNED =
 * material + learned phase-split piece-square tables + learned feature weights. */
enum EvalVariant : int { EVAL_CLASSIC = 0, EVAL_LEARNED = 1 };

/* The learned feature knobs (beyond the piece-square tables). Each has one weight learned
 * from game outcomes; its activation is computed by compute_features(). Mobility is per
 * piece type. Order is fixed — it is the on-disk and snapshot layout after the two tables. */
enum Feature : int {
    FEAT_MOB_N, FEAT_MOB_B, FEAT_MOB_R, FEAT_MOB_Q,   /* legal-move counts, per piece type */
    FEAT_PASSED,                                       /* passed pawns, endgame-weighted     */
    FEAT_ISOLATED,                                     /* isolated pawns                     */
    FEAT_DOUBLED,                                       /* doubled pawns                      */
    FEAT_KING,                                          /* king pawn-shelter, midgame-weighted */
    FEATURE_NB
};

/* Per-handle eval configuration, snapshotted from the global learned weights at
 * engine_create so the search reads a stable copy. The tables are white-relative: a black
 * piece indexes the rank-mirrored square (sq ^ 56). `mg`/`eg` are blended by game phase.
 * Indexed by chess::PieceType (PAWN..KING). Only consulted when variant == EVAL_LEARNED. */
struct EvalParams {
    int variant = EVAL_CLASSIC;
    int mg[chess::PIECE_TYPE_NB][64] = {};
    int eg[chess::PIECE_TYPE_NB][64] = {};
    int featW[FEATURE_NB] = {};
};

/* Internal engine state. One ChessEngine = one game. The transposition table is NOT
 * here: it is the shared g_tt above. */
struct ChessEngine {
    int        skill = 20;   /* 1..20 from the UI; controls search depth */
    EvalParams eval;         /* which evaluation the search uses, plus any learned weights */
};

/* The process-global learned weights: the single source of truth, loaded from disk once and
 * updated in place by training. Engine handles snapshot it at creation; the visualization
 * snapshots it on demand. Guarded by g_weightsMutex for updates/saves (eval reads its own
 * per-handle copy, so it never touches this concurrently). */
struct LearnedWeights {
    int mg[chess::PIECE_TYPE_NB][64] = {};
    int eg[chess::PIECE_TYPE_NB][64] = {};
    int featW[FEATURE_NB] = {};
};

static LearnedWeights g_weights;
static std::mutex     g_weightsMutex;
static std::string    g_weightsPath;

/* Per-game training accumulator (one per learned CPU-vs-CPU game). Records, per ply, where
 * each side's pieces sat (split into midgame/endgame by phase) and each side's feature
 * activations; trainer_apply turns the totals into weight nudges. Squares are white-relative
 * (black indexes sq ^ 56), so a side's tally lines up with the shared white-relative table. */
struct Trainer {
    double mgOcc[chess::COLOR_NB][chess::PIECE_TYPE_NB][64] = {};
    double egOcc[chess::COLOR_NB][chess::PIECE_TYPE_NB][64] = {};
    double featAcc[chess::COLOR_NB][FEATURE_NB] = {};
    int    plies = 0;
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

/* "variant=learned" in the options selects the learned eval; anything else is classic. */
static int parse_variant(const char* options) {
    if (!options) return EVAL_CLASSIC;
    const char* p = std::strstr(options, "variant=");
    if (!p) return EVAL_CLASSIC;
    return std::strncmp(p + 8, "learned", 7) == 0 ? EVAL_LEARNED : EVAL_CLASSIC;
}

/* On-disk format: 6*64 mg ints (PAWN..KING, squares 0..63), then 6*64 eg ints, then
 * FEATURE_NB feature ints, whitespace-separated. A missing file or short read leaves the
 * rest neutral (0), so an absent weights file just means "train from a blank slate".
 * Caller holds g_weightsMutex. */
static void load_global_weights(const char* path) {
    g_weights = LearnedWeights{};                    /* reset to neutral before loading */

    if (!path || !*path) return;
    std::ifstream f(path);
    if (!f) return;

    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq)
            if (!(f >> g_weights.mg[pt][sq])) return;
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq)
            if (!(f >> g_weights.eg[pt][sq])) return;
    for (int i = 0; i < FEATURE_NB; ++i)
        if (!(f >> g_weights.featW[i])) return;
}

/* Persist g_weights to g_weightsPath in the format load_global_weights reads. Caller holds the lock. */
static void save_global_weights() {
    if (g_weightsPath.empty()) return;
    std::ofstream f(g_weightsPath);
    if (!f) return;

    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) f << g_weights.mg[pt][sq] << (sq == 63 ? '\n' : ' ');
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) f << g_weights.eg[pt][sq] << (sq == 63 ? '\n' : ' ');
    for (int i = 0; i < FEATURE_NB; ++i) f << g_weights.featW[i] << (i == FEATURE_NB - 1 ? '\n' : ' ');
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
        score += (6 - squaresToPromotion) * 100; // Bonus for passed pawns, more as they get closer to promotion
    if (isDoubled)
        score -= 20; // Penalty for doubled pawns
    if (isBlocked)
        score -= 20; // Penalty for blocked pawns

    return score;
}

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
 * and the trainer, so the two can never disagree. Constants below are the only tunables. */

/* Per-game-outcome learning rates and clamps. Squares accumulate occupancy (plies on a
 * square, summed); features accumulate normalized per-ply activation (averaged, divided by a
 * nominal scale so high-magnitude mobility doesn't dwarf the small pawn-structure terms). */
static constexpr double SQUARE_LR  = 0.5;
static constexpr int    SQ_CLAMP   = 250;
static constexpr double FEAT_LR    = 2.0;
static constexpr int    FEAT_CLAMP = 500;
static constexpr double FEAT_SCALE[FEATURE_NB] = { 4, 6, 8, 14, 2, 1, 1, 2 };

/* Game phase in [0,1] from remaining non-pawn material (PeSTO weights N=B=1, R=2, Q=4; max
 * 24 for both full sides): 0 = opening, 1 = bare kings. Drives the mg/eg table blend and
 * the phase weighting of the passed-pawn (×phase) and king-safety (×(1−phase)) features. */
static double game_phase(const chess::Position& pos) {
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

/* Fills `out[FEATURE_NB]` with one color's raw feature activations for a position. The piece-
 * square tables handle "where pieces belong"; these capture context a static table can't:
 * legal mobility (per piece type, so pins reduce it), passed pawns (endgame-weighted), pawn
 * structure, and king shelter (midgame-weighted). Ported nowhere — this is the only copy. */
static void compute_features(chess::Position& pos, chess::Color c, double phase, double out[FEATURE_NB]) {
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
        if (front_span_file_only(c, s) & pawns)                          /* doubled (friendly pawn ahead) */
            out[FEAT_DOUBLED] += 1;

        chess::File f = chess::file_of(s);
        chess::Bitboard adjacent = 0;
        if (f > chess::FILE_A) adjacent |= chess::file_bb(chess::File(f - 1));
        if (f < chess::FILE_H) adjacent |= chess::file_bb(chess::File(f + 1));
        if (!(adjacent & pawns))                                          /* isolated */
            out[FEAT_ISOLATED] += 1;
    }

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
static int evaluate_stm(chess::Position& pos, bool whiteToMove, const EvalParams& ep) {
    int s = (ep.variant == EVAL_LEARNED) ? evaluateLearned(pos, ep) : evaluate(pos);
    return whiteToMove ? s : -s;
}

/* Mate scores are "mate in N from THIS node", so they must be re-anchored to the
 * probing node's ply when crossing the TT (store adds ply, retrieve subtracts it).
 * Non-mate scores pass through untouched. */
static int score_to_tt(int s, int ply)   { return s >=  MATE_BOUND ? s + ply : s <= -MATE_BOUND ? s - ply : s; }
static int score_from_tt(int s, int ply) { return s >=  MATE_BOUND ? s - ply : s <= -MATE_BOUND ? s + ply : s; }

/* Heuristic for searching the most promising moves first, which makes alpha-beta prune far
 * more. Bands, highest first: the TT best move, then captures by MVV-LVA (most valuable
 * victim, least valuable attacker), then the two killer moves for this ply (quiet moves that
 * cut a sibling), then the remaining quiet moves. `killers` points at this ply's two-entry
 * slot; `scoreChecks` gates the expensive gives_check term to near-leaf nodes. */
static int order_score(chess::Position& pos, chess::Move m, chess::Move ttMove,
                       const chess::Move* killers, bool scoreChecks) {
    if (m == ttMove)
        return 2000000;                  /* dwarfs any capture/killer/check score below */

    int score = 0;

    if (scoreChecks && pos.gives_check(m))
        score += 1000;

    chess::Piece victim = pos.piece_on(m.to());
#ifdef BENCH_DISABLE_KILLERS
    /* Benchmark A/B only (defined by bench.ps1): the pre-killer ordering — captures by
     * MVV-LVA above quiet moves, no killer band — so the script can time the killer speedup. */
    (void)killers;
    if (victim != chess::NO_PIECE)
        score += 100 + 10 * piece_value(chess::type_of(victim))
                     - piece_value(chess::type_of(pos.piece_on(m.from())));
    else if (m.type() == chess::EN_PASSANT)
        score += 100 + 10 * piece_value(chess::PAWN);
#else
    if (victim != chess::NO_PIECE)
        score += 100000 + 10 * piece_value(chess::type_of(victim))
                        - piece_value(chess::type_of(pos.piece_on(m.from())));
    else if (m.type() == chess::EN_PASSANT)
        score += 100000 + 10 * piece_value(chess::PAWN);
    else if (m == killers[0])
        score += 90000;                  /* quiet move that beta-cut a sibling at this ply */
    else if (m == killers[1])
        score += 80000;
#endif

    return score;
}

/* Sort the move list in place, best-scoring first. Scores are computed once up
 * front so gives_check isn't re-evaluated on every comparison. ttMove may be
 * MOVE_NONE, in which case no move matches it and ordering falls back to captures. */
static void order_moves(chess::Position& pos, chess::MoveList& moves, chess::Move ttMove,
                        const chess::Move* killers, bool scoreChecks) {
    struct ScoredMove { int score; chess::Move move; };
    ScoredMove scored[256];

    for (int i = 0; i < moves.size(); i++)
        scored[i] = { order_score(pos, moves.moves[i], ttMove, killers, scoreChecks), moves.moves[i] };

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
    e->eval.variant = parse_variant(options);
    if (e->eval.variant == EVAL_LEARNED) {
        /* Snapshot the current global weights so the search reads a stable copy (training
         * updates the global between games; the weights path is owned by learned_load). */
        std::lock_guard<std::mutex> lock(g_weightsMutex);
        std::memcpy(e->eval.mg,    g_weights.mg,    sizeof e->eval.mg);
        std::memcpy(e->eval.eg,    g_weights.eg,    sizeof e->eval.eg);
        std::memcpy(e->eval.featW, g_weights.featW, sizeof e->eval.featW);
    }
    return e;
}

CHESS_API int CHESS_CALL engine_set_option(EngineHandle engine,
                                           const char* /*name*/,
                                           const char* /*value*/) {
    if (!engine) return CHESS_ERR_NULL_HANDLE;
    return CHESS_OK;                                      /* TODO: store options */
}

/* Per-search scratch, threaded through the recursion. Kept off global scope so two engine
 * handles can search concurrently without sharing node counts or killer tables. killers[ply]
 * holds up to two quiet moves that recently caused a beta cutoff at that ply; trying them
 * early (right after captures) prunes far more — the quiet-move ordering the search otherwise
 * lacks. */
static constexpr int MAX_PLY = 128;            /* ply never exceeds maxDepth (<= 20) */

struct SearchContext {
    uint64_t          nodes = 0;
    const EvalParams* eval  = nullptr;         /* eval config for this search; set by engine_best_move */
    chess::Move       killers[MAX_PLY][2] = {};/* [ply][slot]; MOVE_NONE until filled */
};

/* Negamax alpha-beta over the shared transposition table. `maxDepth` is the searching
 * bot's difficulty (its root depth); `depth` is remaining depth (draft); `ply` is
 * distance from the root (mate scoring only). Scores are side-to-move-relative.
 * Fail-soft: returns the true best found even outside [alpha, beta]. */
static int negamax(chess::Position& pos, int maxDepth, int depth, int ply,
                   int alpha, int beta, bool whiteToMove, SearchContext& ctx) {
    ctx.nodes++;

    /* A draw is 0 even at the search horizon, and the TT key doesn't encode repetition
     * history, so this must come before both the leaf eval and any TT probe. */
    if (ply > 0 && pos.is_draw())
        return 0;

    if (depth <= 0)
        return evaluate_stm(pos, whiteToMove, *ctx.eval);

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

    order_moves(pos, moves, ttMove, ctx.killers[ply], depth <= 2);

    const int alphaOrig = alpha;
    int best = -INF;
    chess::Move bestMove = chess::MOVE_NONE;

    for (int i = 0; i < moves.size(); i++) {
        chess::Move move = moves.moves[i];
        pos.do_move(move);
        int score = -negamax(pos, maxDepth, depth - 1, ply + 1, -beta, -alpha, !whiteToMove, ctx);
        pos.undo_move(move);

        if (score > best) {
            best = score;
            bestMove = move;
        }
        if (best > alpha)
            alpha = best;
        if (best >= beta) {
            /* A quiet move good enough to fail high here is a strong candidate in sibling
             * lines at this ply — remember it as a killer. pos is back to pre-move state
             * after undo_move, so piece_on(to) still flags a capture correctly. */
            bool isCapture = pos.piece_on(move.to()) != chess::NO_PIECE
                          || move.type() == chess::EN_PASSANT;
            if (!isCapture && ply < MAX_PLY && ctx.killers[ply][0] != move) {
                ctx.killers[ply][1] = ctx.killers[ply][0];
                ctx.killers[ply][0] = move;
            }
            break;                                    /* fail-high cutoff */
        }
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

    SearchContext ctx;
    ctx.eval = &engine->eval;
    int maxDepth = depth_for_skill(engine->skill);
    chess::Move bestMove = moves.moves[0];            /* guaranteed-legal fallback */

    /* Iterative deepening: each depth seeds the next depth's move ordering (via the
     * previous best move and the TT it filled), which makes the deeper search prune
     * far harder than searching to maxDepth cold. */
    for (int d = 1; d <= maxDepth; d++) {
        int alpha = -INF, beta = INF;
        chess::Move iterBest = bestMove;
        int iterScore = -INF;

        order_moves(pos, moves, iterBest, ctx.killers[0], true);

        for (int i = 0; i < moves.size(); i++) {
            chess::Move move = moves.moves[i];
            pos.do_move(move);
            int score = -negamax(pos, maxDepth, d - 1, 1, -beta, -alpha, !whiteToMove, ctx);
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
                     d, static_cast<unsigned long long>(ctx.nodes),
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

/* ---- Learned-weights / training C ABI --------------------------------------------------
 * The managed side orchestrates games but owns no chess logic: it tells the engine where
 * to load/save the global weights, records each played position, and applies the result. */

CHESS_API void CHESS_CALL learned_load(const char* path) {
    std::lock_guard<std::mutex> lock(g_weightsMutex);
    g_weightsPath = path ? path : "";
    load_global_weights(path);
}

CHESS_API int CHESS_CALL weights_snapshot(int* out, int out_len) {
    const int need = 6 * 64 * 2 + FEATURE_NB;     /* mg + eg (PAWN..KING) + features = 776 */
    if (!out || out_len < need) return CHESS_ERR_BUFFER;

    std::lock_guard<std::mutex> lock(g_weightsMutex);
    int n = 0;
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) out[n++] = g_weights.mg[pt][sq];
    for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
        for (int sq = 0; sq < 64; ++sq) out[n++] = g_weights.eg[pt][sq];
    for (int i = 0; i < FEATURE_NB; ++i) out[n++] = g_weights.featW[i];
    return n;
}

CHESS_API TrainerHandle CHESS_CALL trainer_create(void) {
    return new (std::nothrow) Trainer();
}

CHESS_API void CHESS_CALL trainer_record(TrainerHandle t, const char* fen) {
    if (!t || !fen || !*fen) return;
    ensure_initialized();

    chess::Position pos = chess::Position::from_fen(fen);
    double phase = game_phase(pos);

    /* Per-square occupancy, split into midgame/endgame by phase, white-relative. */
    chess::Bitboard occ = pos.pieces();
    while (occ) {
        chess::Square s  = chess::pop_lsb(occ);
        chess::Piece  pc = pos.piece_on(s);
        chess::Color  c  = chess::color_of(pc);
        chess::PieceType pt = chess::type_of(pc);
        int relSq = (c == chess::WHITE) ? int(s) : (int(s) ^ 56);
        t->mgOcc[c][pt][relSq] += (1.0 - phase);
        t->egOcc[c][pt][relSq] += phase;
    }

    /* Per-side feature activations. */
    double w[FEATURE_NB], b[FEATURE_NB];
    compute_features(pos, chess::WHITE, phase, w);
    compute_features(pos, chess::BLACK, phase, b);
    for (int i = 0; i < FEATURE_NB; ++i) {
        t->featAcc[chess::WHITE][i] += w[i];
        t->featAcc[chess::BLACK][i] += b[i];
    }

    t->plies++;
}

CHESS_API void CHESS_CALL trainer_apply(TrainerHandle t, int winner, double weight) {
    if (!t) return;

    std::lock_guard<std::mutex> lock(g_weightsMutex);

    /* pass 0 = winner (reward, +1); pass 1 = loser (punish, -1). */
    for (int pass = 0; pass < 2; ++pass) {
        chess::Color side = chess::Color((pass == 0 ? winner : (winner ^ 1)) & 1);
        int sign = pass == 0 ? 1 : -1;

        for (int pt = chess::PAWN; pt <= chess::KING; ++pt)
            for (int sq = 0; sq < 64; ++sq) {
                if (t->mgOcc[side][pt][sq] != 0.0) {
                    int d = sign * int(std::lround(SQUARE_LR * t->mgOcc[side][pt][sq] * weight));
                    g_weights.mg[pt][sq] = std::clamp(g_weights.mg[pt][sq] + d, -SQ_CLAMP, SQ_CLAMP);
                }
                if (t->egOcc[side][pt][sq] != 0.0) {
                    int d = sign * int(std::lround(SQUARE_LR * t->egOcc[side][pt][sq] * weight));
                    g_weights.eg[pt][sq] = std::clamp(g_weights.eg[pt][sq] + d, -SQ_CLAMP, SQ_CLAMP);
                }
            }

        if (t->plies > 0)
            for (int i = 0; i < FEATURE_NB; ++i) {
                double avg = t->featAcc[side][i] / t->plies;        /* per-ply average, normalized */
                int d = sign * int(std::lround(FEAT_LR * (avg / FEAT_SCALE[i]) * weight));
                g_weights.featW[i] = std::clamp(g_weights.featW[i] + d, -FEAT_CLAMP, FEAT_CLAMP);
            }
    }

    save_global_weights();
}

CHESS_API void CHESS_CALL trainer_destroy(TrainerHandle t) {
    delete t;                                             /* delete nullptr is safe */
}

} /* extern "C" */
