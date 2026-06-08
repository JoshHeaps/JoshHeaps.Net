// Legal move generation. generate_legal is a method on Position (declared
// there); this header exists so other translation units can pull in the
// pseudo-legal generator if they ever want it.
#ifndef CHESS_MOVEGEN_H
#define CHESS_MOVEGEN_H

#include "position.h"

namespace chess {

// Generates pseudo-legal moves (ignores leaving your own king in check).
// Position::generate_legal filters these. Castling is generated fully-legal.
void generate_pseudo(const Position& pos, MoveList& list);

} // namespace chess

#endif // CHESS_MOVEGEN_H
