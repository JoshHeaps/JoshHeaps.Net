/* eval.h - position evaluation (classic + learned) and feature computation.
 *
 * This is the shared hub of the engine's "scoring" logic. The learned model's
 * feature activations (compute_features) and game phase are used by BOTH the eval
 * here and the trainer (learned_model.cpp), so they live in one place and can never
 * diverge. The search (search.cpp) consumes evaluate_stm and piece_value. */
#pragma once

#include "types.h"
#include "position.h"

/* Eval variant for an engine handle. CLASSIC = the hand-crafted evaluate(); LEARNED =
 * material + learned phase-split piece-square tables + learned feature weights. */
enum EvalVariant : int { EVAL_CLASSIC = 0, EVAL_LEARNED = 1 };

/* The learned feature knobs (beyond the piece-square tables). Each has one weight learned
 * from game outcomes; its activation is computed by compute_features(). Mobility is per
 * piece type. Order is fixed — it is the on-disk and snapshot layout after the two tables. */
enum Feature : int {
    FEAT_MOB_N, FEAT_MOB_B, FEAT_MOB_R, FEAT_MOB_Q,   /* legal-move counts, per piece type */
    FEAT_PASSED,                                       /* passed pawns, endgame-weighted     */
    FEAT_PAWN_LINK,                                     /* pawns defended by a friendly pawn  */
    FEAT_KING,                                          /* king pawn-shelter, midgame-weighted */
    FEATURE_NB
};

/* Per-feature nominal scale: feature activations are divided by this before being weighted,
 * so high-magnitude mobility doesn't dwarf the small pawn-structure terms. Used by both the
 * learned eval (to combine) and the trainer (to normalize activations), so it lives here. */
inline constexpr double FEAT_SCALE[FEATURE_NB] = { 4, 6, 8, 14, 2, 3, 2 };

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

/* Centipawn material value of a piece type (0 for king / none). Shared with the search's
 * MVV-LVA move ordering. */
int piece_value(chess::PieceType pt);

/* Game phase in [0,1] from remaining non-pawn material: 0 = opening, 1 = bare kings. Drives
 * the mg/eg table blend and the phase weighting of the passed-pawn / king-safety features. */
double game_phase(const chess::Position& pos);

/* Fills `out[FEATURE_NB]` with one color's raw feature activations for a position (mobility,
 * pawn structure, king shelter). The single source of feature activations, shared by the
 * learned eval and the trainer. Non-const because mobility generates legal moves. */
void compute_features(chess::Position& pos, chess::Color c, double phase, double out[FEATURE_NB]);

/* Side-to-move-relative evaluation for negamax (positive = good for whoever is to move).
 * Dispatches to the classic or learned eval per ep.variant. Non-const because the learned
 * eval computes mobility via the move generator. */
int evaluate_stm(chess::Position& pos, bool whiteToMove, const EvalParams& ep);
