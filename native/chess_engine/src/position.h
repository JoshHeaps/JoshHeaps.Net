// The board. Hybrid representation: bitboards (per piece type and per color)
// for fast generation/attacks, plus a piece-on-square mailbox for O(1)
// "what's here?" queries. do_move/undo_move keep both in sync, along with the
// Zobrist key. One Position is one game line; it is freely copyable.
#ifndef CHESS_POSITION_H
#define CHESS_POSITION_H

#include "types.h"
#include "bitboard.h"

#include <string>
#include <string_view>

namespace chess {

class Position {
public:
    /// Parse a FEN string into a position.
    static Position from_fen(std::string_view fen);
    /// Serialize back to FEN.
    std::string to_fen() const;

    // --- mailbox queries ---
    Piece  piece_on(Square s)  const { return board[s]; }
    bool   empty(Square s)     const { return board[s] == NO_PIECE; }
    Color  side_to_move()      const { return sideToMove; }
    Square ep_square()         const { return epSquare; }
    int    halfmove_clock()    const { return rule50; }
    int    fullmove_number()   const { return 1 + gamePly / 2; }
    bool   can_castle(Color c, CastlingSide side) const;
    Square king_square(Color c) const { return lsb(pieces(c, KING)); }

    // --- bitboard accessors ---
    Bitboard pieces()                      const { return byColorBB[WHITE] | byColorBB[BLACK]; }
    Bitboard pieces(Color c)               const { return byColorBB[c]; }
    Bitboard pieces(PieceType pt)          const { return byTypeBB[pt]; }
    Bitboard pieces(Color c, PieceType pt) const { return byTypeBB[pt] & byColorBB[c]; }

    // --- attacks / checks ---
    Bitboard attackers_to(Square s) const { return attackers_to(s, pieces()); }
    Bitboard attackers_to(Square s, Bitboard occ) const;
    bool     in_check() const;                 // is side_to_move in check?
    bool     gives_check(Move m);              // does m check the opponent?

    // --- the three you asked for ---
    void generate_legal(MoveList& list);       // defined in movegen.cpp
    void do_move(Move m);
    void undo_move(Move m);

    // Seed prior-position keys (oldest first, excluding the current position) so
    // is_draw() can see game history the FEN doesn't carry. Call once, right after
    // from_fen and before any do_move.
    void seed_history(const uint64_t* priorKeys, int count);

    // --- freebies ---
    uint64_t key()     const { return zkey; }
    bool     is_draw() const;                  // 50-move + threefold + insufficient material
    void     print()   const;

private:
    void put_piece(Piece pc, Square s);
    void remove_piece(Square s);
    void move_piece(Square from, Square to);
    bool insufficient_material() const;

    Bitboard byTypeBB[PIECE_TYPE_NB];
    Bitboard byColorBB[COLOR_NB];
    Piece    board[SQUARE_NB];
    Color    sideToMove;
    int      castlingRights;
    Square   epSquare;
    int      rule50;
    int      gamePly;
    uint64_t zkey;

    struct Undo {
        int      castlingRights;
        Square   epSquare;
        int      rule50;
        uint64_t key;
        Piece    captured;
    };
    Undo undoStack[1024];
    int  undoCount;

    uint64_t repKeys[1024];
    int      repCount;
};

} // namespace chess

#endif // CHESS_POSITION_H
