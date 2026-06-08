// Perft: counts the leaf nodes of the legal move tree to a given depth.
// The standard correctness test for move generation + make/unmake.
#ifndef CHESS_PERFT_H
#define CHESS_PERFT_H

#include "position.h"

namespace chess {

uint64_t perft(Position& pos, int depth);

} // namespace chess

#endif // CHESS_PERFT_H
