/* search.h - the engine's search: a single entry point.
 *
 * Everything else (the shared transposition table, move ordering, negamax, and the
 * iterative-deepening driver) is an implementation detail of search.cpp. */
#pragma once

#include "position.h"
#include "eval.h"

/* Best move for `pos` using evaluation `ep`, searched to the depth implied by `skill`
 * (1..20). Seeds, allocates, and reuses the process-wide transposition table on first
 * call. Returns chess::MOVE_NONE when there is no legal move (mate/stalemate). The
 * position's repetition/50-move history should already be seeded by the caller. */
chess::Move find_best_move(chess::Position& pos, const EvalParams& ep, int skill);
