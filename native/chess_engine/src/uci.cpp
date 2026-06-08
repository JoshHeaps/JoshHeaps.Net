#include "uci.h"

#include <cstdlib>

namespace chess {

std::string move_to_uci(Move m) {
    if (m == MOVE_NONE) return "0000";

    Square f = m.from(), t = m.to();
    std::string s;
    s += char('a' + file_of(f));
    s += char('1' + rank_of(f));
    s += char('a' + file_of(t));
    s += char('1' + rank_of(t));

    if (m.type() == PROMOTION) {
        static const char promo[PIECE_TYPE_NB] = { 0, 0, 'n', 'b', 'r', 'q', 0 };
        s += promo[m.promotion()];
    }
    return s;
}

Move move_from_uci(const Position& pos, std::string_view uci) {
    if (uci.size() < 4) return MOVE_NONE;

    Square from = make_square(File(uci[0] - 'a'), Rank(uci[1] - '1'));
    Square to   = make_square(File(uci[2] - 'a'), Rank(uci[3] - '1'));

    if (uci.size() >= 5) {
        PieceType promo = QUEEN;
        switch (uci[4]) {
            case 'q': promo = QUEEN;  break;
            case 'r': promo = ROOK;   break;
            case 'b': promo = BISHOP; break;
            case 'n': promo = KNIGHT; break;
        }
        return Move::make(from, to, PROMOTION, promo);
    }

    Piece pc = pos.piece_on(from);
    if (type_of(pc) == KING && std::abs(int(to) - int(from)) == 2)
        return Move::make(from, to, CASTLING);
    if (type_of(pc) == PAWN && to == pos.ep_square() && file_of(from) != file_of(to))
        return Move::make(from, to, EN_PASSANT);

    return Move::make(from, to);
}

} // namespace chess
