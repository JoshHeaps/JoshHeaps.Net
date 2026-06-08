// Standalone perft harness: validates move generation + make/unmake against
// published node counts. Build separately from the DLL (see build instructions
// in the repo); not part of the shipped library.
#include "../src/bitboard.h"
#include "../src/zobrist.h"
#include "../src/position.h"
#include "../src/perft.h"

#include <cstdint>
#include <cstdio>

using namespace chess;

struct Case {
    const char* name;
    const char* fen;
    int         depth;
    uint64_t    expected;
};

// Verifies the incrementally-maintained key matches a from-scratch hash of the
// same position (also exercises to_fen -> from_fen round-tripping).
static uint64_t verify_keys(Position& pos, int depth) {
    uint64_t mismatches = 0;
    if (pos.key() != Position::from_fen(pos.to_fen()).key())
        ++mismatches;
    if (depth == 0) return mismatches;

    MoveList list;
    pos.generate_legal(list);
    for (Move m : list) {
        pos.do_move(m);
        mismatches += verify_keys(pos, depth - 1);
        pos.undo_move(m);
    }
    return mismatches;
}

int main() {
    init_bitboards();
    Zobrist::init();

    const Case cases[] = {
        {"startpos d5", "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", 5, 4865609ULL},
        {"kiwipete d4", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1", 4, 4085603ULL},
        {"position3 d5", "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1", 5, 674624ULL},
        {"position4 d4", "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1", 4, 422333ULL},
        {"position5 d4", "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8", 4, 2103487ULL},
        {"position6 d4", "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10", 4, 3894594ULL},
    };

    int fails = 0;
    for (const Case& c : cases) {
        Position pos = Position::from_fen(c.fen);
        uint64_t got = perft(pos, c.depth);
        bool ok = (got == c.expected);
        std::printf("%-14s %14llu  expected %14llu  %s\n",
                    c.name, (unsigned long long)got, (unsigned long long)c.expected,
                    ok ? "OK" : "FAIL");
        if (!ok) ++fails;
    }

    std::printf("\n%s\n", fails ? "*** PERFT FAILED ***" : "ALL PERFT PASSED");

    // Zobrist key + FEN round-trip consistency.
    const char* startfen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    const char* kiwifen  = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";
    Position a = Position::from_fen(startfen);
    Position b = Position::from_fen(kiwifen);
    uint64_t km = verify_keys(a, 4) + verify_keys(b, 3);
    std::printf("key/fen mismatches: %llu  %s\n", (unsigned long long)km,
                km == 0 ? "OK" : "FAIL");
    if (km) ++fails;

    return fails ? 1 : 0;
}
