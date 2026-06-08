// Conversions between moves and UCI long-algebraic strings ("e2e4", "e7e8q").
// move_from_uci resolves the move's flag (castling / en passant / promotion)
// against the given position.
#ifndef CHESS_UCI_H
#define CHESS_UCI_H

#include "position.h"

#include <string>
#include <string_view>

namespace chess {

std::string move_to_uci(Move m);
Move        move_from_uci(const Position& pos, std::string_view uci);

} // namespace chess

#endif // CHESS_UCI_H
