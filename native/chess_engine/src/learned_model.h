/* learned_model.h - the learned engine's process-global weights and training.
 *
 * Owns the single source of truth for the learned weights (loaded from / saved to disk),
 * the read-only snapshot for visualization, and the per-game training accumulator that
 * turns played positions + a result into weight nudges. The DLL ABI (chess_engine.cpp)
 * is a thin pass-through to the functions here; feature/phase math is shared from eval.h. */
#pragma once

#include "eval.h"

/* Per-game training accumulator. Global-namespace `Trainer` so it matches the opaque
 * `typedef struct Trainer* TrainerHandle` in the public ABI header. Defined in the .cpp. */
struct Trainer;

namespace learned {

/* Set the global weights file path and load from it (idempotent; a missing/short file
 * leaves the weights neutral). */
void load(const char* path);

/* Copy the global weights out for visualization: 6*64 midgame + 6*64 endgame + features.
 * Returns the count written, or CHESS_ERR_BUFFER if out_len is too small (needs >= 776). */
int snapshot(int* out, int out_len);

/* Snapshot the current global weights into a fresh engine handle's eval config so the
 * search reads a stable copy (training updates the global between games). */
void copy_weights_to(EvalParams& ep);

/* Per-game training lifecycle. */
Trainer* create();
void     destroy(Trainer* t);

/* Record one played position (post-move FEN) into the accumulator. */
void record(Trainer* t, const char* fen);

/* Apply a finished game's outcome to the global weights and persist: rewards the winner's
 * occupied squares / features, punishes the loser's, scaled by `weight`. winner: 0=W, 1=B. */
void apply(Trainer* t, int winner, double weight);

}  // namespace learned
