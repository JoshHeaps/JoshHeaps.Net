#include "zobrist.h"

namespace chess {
namespace Zobrist {

uint64_t psq[PIECE_NB][SQUARE_NB];
uint64_t enpassant[FILE_NB];
uint64_t castling[16];
uint64_t side;

namespace {
struct PRNG {
    uint64_t s;
    explicit PRNG(uint64_t seed) : s(seed) {}
    uint64_t next() {
        s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
        return s * 2685821657736338717ULL;
    }
};
}

void init() {
    PRNG rng(0xC0FFEE123456789Aull);

    for (int p = 0; p < PIECE_NB; ++p)
        for (int s = 0; s < SQUARE_NB; ++s)
            psq[p][s] = rng.next();

    for (int f = 0; f < FILE_NB; ++f)
        enpassant[f] = rng.next();

    for (int c = 0; c < 16; ++c)
        castling[c] = rng.next();

    side = rng.next();
}

} // namespace Zobrist
} // namespace chess
