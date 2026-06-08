// Zobrist hashing keys. Filled once by Zobrist::init() (from engine_create).
// Position maintains the running key incrementally in do_move/undo_move.
#ifndef CHESS_ZOBRIST_H
#define CHESS_ZOBRIST_H

#include "types.h"

namespace chess {
namespace Zobrist {

extern uint64_t psq[PIECE_NB][SQUARE_NB];
extern uint64_t enpassant[FILE_NB];
extern uint64_t castling[16];
extern uint64_t side;

void init();

} // namespace Zobrist
} // namespace chess

#endif // CHESS_ZOBRIST_H
