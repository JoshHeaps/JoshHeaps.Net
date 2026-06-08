// Core vocabulary for the chess engine: squares, pieces, moves.
// Everything else is built on these. Convention: A1 = 0 ... H8 = 63,
// file = square & 7 (A..H), rank = square >> 3 (1..8). North = +8.
#ifndef CHESS_TYPES_H
#define CHESS_TYPES_H

#include <cstdint>

namespace chess {

using Bitboard = uint64_t;

enum Color : int { WHITE, BLACK, COLOR_NB = 2 };

enum PieceType : int {
    NO_PIECE_TYPE, PAWN, KNIGHT, BISHOP, ROOK, QUEEN, KING, PIECE_TYPE_NB = 8
};

enum Piece : int {
    NO_PIECE,
    W_PAWN = PAWN, W_KNIGHT, W_BISHOP, W_ROOK, W_QUEEN, W_KING,
    B_PAWN = PAWN + 8, B_KNIGHT, B_BISHOP, B_ROOK, B_QUEEN, B_KING,
    PIECE_NB = 16
};

enum Square : int {
    A1, B1, C1, D1, E1, F1, G1, H1,
    A2, B2, C2, D2, E2, F2, G2, H2,
    A3, B3, C3, D3, E3, F3, G3, H3,
    A4, B4, C4, D4, E4, F4, G4, H4,
    A5, B5, C5, D5, E5, F5, G5, H5,
    A6, B6, C6, D6, E6, F6, G6, H6,
    A7, B7, C7, D7, E7, F7, G7, H7,
    A8, B8, C8, D8, E8, F8, G8, H8,
    SQ_NONE,
    SQUARE_NB = 64
};

enum File : int { FILE_A, FILE_B, FILE_C, FILE_D, FILE_E, FILE_F, FILE_G, FILE_H, FILE_NB = 8 };
enum Rank : int { RANK_1, RANK_2, RANK_3, RANK_4, RANK_5, RANK_6, RANK_7, RANK_8, RANK_NB = 8 };

enum CastlingSide : int { KINGSIDE, QUEENSIDE };

// Castling rights as a bitmask.
enum CastlingRights : int {
    NO_CASTLING = 0,
    WHITE_OO = 1, WHITE_OOO = 2,
    BLACK_OO = 4, BLACK_OOO = 8,
    ANY_CASTLING = 15
};

constexpr Color operator~(Color c) { return Color(c ^ BLACK); }

constexpr Square make_square(File f, Rank r) { return Square((r << 3) + f); }
constexpr File   file_of(Square s)           { return File(s & 7); }
constexpr Rank   rank_of(Square s)           { return Rank(s >> 3); }

constexpr Piece     make_piece(Color c, PieceType pt) { return Piece((c << 3) + pt); }
constexpr PieceType type_of(Piece p)                  { return PieceType(p & 7); }
constexpr Color     color_of(Piece p)                 { return Color(p >> 3); } // assumes p != NO_PIECE

// A move packed into 16 bits: from:6 | to:6 | promotion:2 | flag:2.
// The promotion bits encode KNIGHT..QUEEN as 0..3 and are only meaningful
// when the flag is PROMOTION.
enum MoveFlag : int { NORMAL, PROMOTION, EN_PASSANT, CASTLING };

struct Move {
    uint16_t data;

    constexpr Move() : data(0) {}
    constexpr explicit Move(uint16_t d) : data(d) {}

    /// Build a move. `promo` only matters when `flag == PROMOTION`.
    static constexpr Move make(Square from, Square to, MoveFlag flag = NORMAL,
                               PieceType promo = KNIGHT) {
        return Move(uint16_t((flag << 14) | ((promo - KNIGHT) << 12) | (to << 6) | from));
    }

    constexpr Square    from()      const { return Square(data & 0x3F); }
    constexpr Square    to()        const { return Square((data >> 6) & 0x3F); }
    constexpr MoveFlag  type()      const { return MoveFlag((data >> 14) & 0x3); }
    constexpr PieceType promotion() const { return PieceType(((data >> 12) & 0x3) + KNIGHT); }

    constexpr bool operator==(Move m) const { return data == m.data; }
    constexpr bool operator!=(Move m) const { return data != m.data; }
};

// A1->A1 is never a real move, so an all-zero move is our "none" sentinel.
constexpr Move MOVE_NONE = Move(0);

// Fixed-capacity, allocation-free, range-for friendly. 256 covers any legal position.
struct MoveList {
    Move moves[256];
    int  count = 0;

    void add(Move m) { moves[count++] = m; }
    int  size() const { return count; }

    Move*       begin()       { return moves; }
    Move*       end()         { return moves + count; }
    const Move* begin() const { return moves; }
    const Move* end()   const { return moves + count; }
};

} // namespace chess

#endif // CHESS_TYPES_H
