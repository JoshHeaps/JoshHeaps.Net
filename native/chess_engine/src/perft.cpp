#include "perft.h"

namespace chess {

uint64_t perft(Position& pos, int depth) {
    if (depth == 0) return 1;

    MoveList list;
    pos.generate_legal(list);

    if (depth == 1) return uint64_t(list.size());

    uint64_t nodes = 0;
    for (Move m : list) {
        pos.do_move(m);
        nodes += perft(pos, depth - 1);
        pos.undo_move(m);
    }
    return nodes;
}

} // namespace chess
