#include "bitboard.h"

#include <cstdlib>

namespace chess {

Bitboard PawnAttacks[COLOR_NB][SQUARE_NB];
Bitboard KnightAttacks[SQUARE_NB];
Bitboard KingAttacks[SQUARE_NB];

Magic BishopMagics[SQUARE_NB];
Magic RookMagics[SQUARE_NB];

// Backing storage the magics index into (fancy-magic sizes).
static Bitboard RookTable[102400];
static Bitboard BishopTable[5248];

namespace {

int file_distance(Square a, Square b) {
    return std::abs(int(file_of(a)) - int(file_of(b)));
}

// Slow, edge-aware ray attack used only to build the tables.
Bitboard sliding_attack(const int* dirs, Square sq, Bitboard occ) {
    Bitboard attacks = 0;
    for (int i = 0; i < 4; ++i) {
        Square prev = sq;
        int t = int(sq) + dirs[i];
        while (t >= 0 && t < 64 && file_distance(Square(t), prev) <= 1) {
            attacks |= square_bb(Square(t));
            if (occ & square_bb(Square(t))) break;
            prev = Square(t);
            t += dirs[i];
        }
    }
    return attacks;
}

// Relevant-occupancy mask: the ray squares excluding board edges.
Bitboard slider_mask(const int* dirs, Square sq) {
    Bitboard edges = ((RANK_1_BB | RANK_8_BB) & ~rank_bb(rank_of(sq)))
                   | ((FILE_A_BB | FILE_H_BB) & ~file_bb(file_of(sq)));
    return sliding_attack(dirs, sq, 0) & ~edges;
}

// Deterministic xorshift PRNG (fixed seed -> reproducible magics).
struct PRNG {
    uint64_t s;
    explicit PRNG(uint64_t seed) : s(seed) {}
    uint64_t next() {
        s ^= s >> 12; s ^= s << 25; s ^= s >> 27;
        return s * 2685821657736338717ULL;
    }
    // Few set bits -> better magic candidates.
    uint64_t sparse() { return next() & next() & next(); }
};

void init_magics(const int* dirs, Magic magics[], Bitboard table[]) {
    PRNG rng(0x9E3779B97F4A7C15ull); // fixed seed -> reproducible magics

    Bitboard occupancy[4096];
    Bitboard reference[4096];
    int      epoch[4096] = {};
    int      currentEpoch = 0;

    size_t offset = 0;
    for (int sq = 0; sq < 64; ++sq) {
        Magic& m = magics[sq];
        m.mask  = slider_mask(dirs, Square(sq));
        m.shift = 64 - popcount(m.mask);
        m.attacks = table + offset;

        // Enumerate every subset of the mask (Carry-Rippler).
        Bitboard b = 0;
        int size = 0;
        do {
            occupancy[size] = b;
            reference[size] = sliding_attack(dirs, Square(sq), b);
            ++size;
            b = (b - m.mask) & m.mask;
        } while (b);

        // Search for a magic that maps subsets to indices collision-free
        // (collisions are fine only when the attack set is identical).
        for (;;) {
            Bitboard magic;
            do {
                magic = rng.sparse();
            } while (popcount((m.mask * magic) >> 56) < 6);

            m.magic = magic;
            ++currentEpoch;
            bool ok = true;
            for (int i = 0; i < size; ++i) {
                unsigned idx = m.index(occupancy[i]);
                if (epoch[idx] < currentEpoch) {
                    epoch[idx] = currentEpoch;
                    m.attacks[idx] = reference[i];
                } else if (m.attacks[idx] != reference[i]) {
                    ok = false;
                    break;
                }
            }
            if (ok) break;
        }

        offset += size;
    }
}

} // namespace

void init_bitboards() {
    for (int s = 0; s < 64; ++s) {
        Bitboard b = square_bb(Square(s));

        PawnAttacks[WHITE][s] = ((b & ~FILE_H_BB) << 9) | ((b & ~FILE_A_BB) << 7);
        PawnAttacks[BLACK][s] = ((b & ~FILE_A_BB) >> 9) | ((b & ~FILE_H_BB) >> 7);

        const int knightDirs[8] = { 17, 15, 10, 6, -6, -10, -15, -17 };
        Bitboard kn = 0;
        for (int d : knightDirs) {
            int t = s + d;
            if (t >= 0 && t < 64 && file_distance(Square(t), Square(s)) <= 2)
                kn |= square_bb(Square(t));
        }
        KnightAttacks[s] = kn;

        const int kingDirs[8] = { 8, -8, 1, -1, 9, 7, -7, -9 };
        Bitboard kg = 0;
        for (int d : kingDirs) {
            int t = s + d;
            if (t >= 0 && t < 64 && file_distance(Square(t), Square(s)) <= 1)
                kg |= square_bb(Square(t));
        }
        KingAttacks[s] = kg;
    }

    const int rookDirs[4]   = { 8, -8, 1, -1 };
    const int bishopDirs[4] = { 9, 7, -7, -9 };
    init_magics(rookDirs, RookMagics, RookTable);
    init_magics(bishopDirs, BishopMagics, BishopTable);
}

} // namespace chess
