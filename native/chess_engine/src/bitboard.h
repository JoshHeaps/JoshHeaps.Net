// Bitboard utilities and precomputed attack tables. Sliders use magic
// bitboards; the tables are built once by init_bitboards() (called from
// engine_create) and are read-only afterwards.
#ifndef CHESS_BITBOARD_H
#define CHESS_BITBOARD_H

#include "types.h"

#if defined(_MSC_VER)
#include <intrin.h>
#endif

namespace chess {

constexpr Bitboard FILE_A_BB = 0x0101010101010101ULL;
constexpr Bitboard FILE_H_BB = 0x8080808080808080ULL;
constexpr Bitboard RANK_1_BB = 0x00000000000000FFULL;
constexpr Bitboard RANK_8_BB = 0xFF00000000000000ULL;

inline Bitboard square_bb(Square s) { return 1ULL << s; }
inline Bitboard file_bb(File f)     { return FILE_A_BB << f; }
inline Bitboard rank_bb(Rank r)     { return RANK_1_BB << (8 * int(r)); }

inline int popcount(Bitboard b) {
#if defined(_MSC_VER)
    return int(__popcnt64(b));
#else
    return __builtin_popcountll(b);
#endif
}

inline Square lsb(Bitboard b) {
#if defined(_MSC_VER)
    unsigned long i;
    _BitScanForward64(&i, b);
    return Square(i);
#else
    return Square(__builtin_ctzll(b));
#endif
}

// Returns the least-significant square and clears it from b.
inline Square pop_lsb(Bitboard& b) {
    Square s = lsb(b);
    b &= b - 1;
    return s;
}

inline bool more_than_one(Bitboard b) { return b & (b - 1); }

// Precomputed leaper attacks (filled by init_bitboards).
extern Bitboard PawnAttacks[COLOR_NB][SQUARE_NB];
extern Bitboard KnightAttacks[SQUARE_NB];
extern Bitboard KingAttacks[SQUARE_NB];

struct Magic {
    Bitboard  mask;
    Bitboard  magic;
    Bitboard* attacks;
    unsigned  shift;

    unsigned index(Bitboard occ) const {
        return unsigned(((occ & mask) * magic) >> shift);
    }
};

extern Magic BishopMagics[SQUARE_NB];
extern Magic RookMagics[SQUARE_NB];

inline Bitboard bishop_attacks(Square s, Bitboard occ) {
    const Magic& m = BishopMagics[s];
    return m.attacks[m.index(occ)];
}
inline Bitboard rook_attacks(Square s, Bitboard occ) {
    const Magic& m = RookMagics[s];
    return m.attacks[m.index(occ)];
}
inline Bitboard queen_attacks(Square s, Bitboard occ) {
    return bishop_attacks(s, occ) | rook_attacks(s, occ);
}

// Must be called once before any attack query (engine_create does this).
void init_bitboards();

} // namespace chess

#endif // CHESS_BITBOARD_H
