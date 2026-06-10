/* Search benchmark harness. Drives engine_best_move on a fixed set of tactical positions
 * and lets the engine print its per-depth node counts (to stderr). Run ONE position per
 * process so each search starts with a cold transposition table — the shared TT is global
 * and would otherwise carry over between positions and skew the counts. bench.ps1 loops the
 * indices for you and tabulates the deepest line per position.
 *
 *   bench [index] [skill]
 *     index : position to run (0-based). Omit to run them all in this one process.
 *     skill : search difficulty / max depth (default 8). */
#include "chess_engine.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>

namespace {

struct Position { const char* name; const char* fen; };

const Position kPositions[] = {
    { "kiwipete", "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1" },
    { "ruy",      "r1bqkbnr/pppp1ppp/2n5/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 1"     },
    { "sicilian", "2rq1rk1/pp1bppbp/2np1np1/8/3NP3/2N1BP2/PPPQ2PP/2KR1B1R w - - 0 1"       },
};
const int kPositionCount = static_cast<int>(sizeof(kPositions) / sizeof(kPositions[0]));

void run(int index, const char* options) {
    EngineHandle engine = engine_create(options);
    if (!engine) {
        std::fprintf(stderr, "engine_create failed\n");
        return;
    }

    char move[16];
    std::fprintf(stderr, "### %d %s\n", index, kPositions[index].name);

    auto start = std::chrono::steady_clock::now();
    int rc = engine_best_move(engine, kPositions[index].fen, "", move, sizeof(move));
    auto elapsed = std::chrono::steady_clock::now() - start;
    long long ms = std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count();

    std::fprintf(stderr, "rc=%d best=%s time_ms=%lld\n", rc, move, ms);

    engine_destroy(engine);
}

} // namespace

int main(int argc, char** argv) {
    int index = argc > 1 ? std::atoi(argv[1]) : -1;
    int skill = argc > 2 ? std::atoi(argv[2]) : 8;

    char options[32];
    std::snprintf(options, sizeof(options), "skill=%d", skill);

    if (index >= 0 && index < kPositionCount) {
        run(index, options);
        return 0;
    }

    for (int i = 0; i < kPositionCount; i++)
        run(i, options);
    return 0;
}
