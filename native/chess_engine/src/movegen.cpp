#include "movegen.h"

namespace chess {

namespace {

void add_promotions(MoveList& list, Square from, Square to) {
    list.add(Move::make(from, to, PROMOTION, QUEEN));
    list.add(Move::make(from, to, PROMOTION, ROOK));
    list.add(Move::make(from, to, PROMOTION, BISHOP));
    list.add(Move::make(from, to, PROMOTION, KNIGHT));
}

void generate_castling(const Position& pos, MoveList& list) {
    if (pos.in_check()) return;

    Color us = pos.side_to_move(), them = ~us;
    Bitboard occ = pos.pieces();
    auto attacked = [&](Square sq) { return (pos.attackers_to(sq) & pos.pieces(them)) != 0; };

    if (us == WHITE) {
        if (pos.can_castle(WHITE, KINGSIDE) &&
            !(occ & (square_bb(F1) | square_bb(G1))) && !attacked(F1) && !attacked(G1))
            list.add(Move::make(E1, G1, CASTLING));
        if (pos.can_castle(WHITE, QUEENSIDE) &&
            !(occ & (square_bb(B1) | square_bb(C1) | square_bb(D1))) && !attacked(D1) && !attacked(C1))
            list.add(Move::make(E1, C1, CASTLING));
    } else {
        if (pos.can_castle(BLACK, KINGSIDE) &&
            !(occ & (square_bb(F8) | square_bb(G8))) && !attacked(F8) && !attacked(G8))
            list.add(Move::make(E8, G8, CASTLING));
        if (pos.can_castle(BLACK, QUEENSIDE) &&
            !(occ & (square_bb(B8) | square_bb(C8) | square_bb(D8))) && !attacked(D8) && !attacked(C8))
            list.add(Move::make(E8, C8, CASTLING));
    }
}

} // namespace

void generate_pseudo(const Position& pos, MoveList& list) {
    Color us = pos.side_to_move(), them = ~us;
    Bitboard occ = pos.pieces();
    Bitboard targets = ~pos.pieces(us);  // empty squares or enemy pieces
    Bitboard theirs = pos.pieces(them);

    // Pawns
    int  push = (us == WHITE) ? 8 : -8;
    Rank promoRank = (us == WHITE) ? RANK_8 : RANK_1;
    Rank startRank = (us == WHITE) ? RANK_2 : RANK_7;
    Bitboard b = pos.pieces(us, PAWN);
    while (b) {
        Square s = pop_lsb(b);
        Square t = Square(int(s) + push);
        if (!(occ & square_bb(t))) {
            if (rank_of(t) == promoRank) {
                add_promotions(list, s, t);
            } else {
                list.add(Move::make(s, t));
                if (rank_of(s) == startRank) {
                    Square t2 = Square(int(t) + push);
                    if (!(occ & square_bb(t2))) list.add(Move::make(s, t2));
                }
            }
        }
        Bitboard caps = PawnAttacks[us][s] & theirs;
        while (caps) {
            Square c = pop_lsb(caps);
            if (rank_of(c) == promoRank) add_promotions(list, s, c);
            else                         list.add(Move::make(s, c));
        }
        if (pos.ep_square() != SQ_NONE && (PawnAttacks[us][s] & square_bb(pos.ep_square())))
            list.add(Move::make(s, pos.ep_square(), EN_PASSANT));
    }

    // Knights
    b = pos.pieces(us, KNIGHT);
    while (b) {
        Square s = pop_lsb(b);
        Bitboard a = KnightAttacks[s] & targets;
        while (a) list.add(Move::make(s, pop_lsb(a)));
    }

    // Bishops
    b = pos.pieces(us, BISHOP);
    while (b) {
        Square s = pop_lsb(b);
        Bitboard a = bishop_attacks(s, occ) & targets;
        while (a) list.add(Move::make(s, pop_lsb(a)));
    }

    // Rooks
    b = pos.pieces(us, ROOK);
    while (b) {
        Square s = pop_lsb(b);
        Bitboard a = rook_attacks(s, occ) & targets;
        while (a) list.add(Move::make(s, pop_lsb(a)));
    }

    // Queens
    b = pos.pieces(us, QUEEN);
    while (b) {
        Square s = pop_lsb(b);
        Bitboard a = queen_attacks(s, occ) & targets;
        while (a) list.add(Move::make(s, pop_lsb(a)));
    }

    // King (non-castling)
    {
        Square s = pos.king_square(us);
        Bitboard a = KingAttacks[s] & targets;
        while (a) list.add(Move::make(s, pop_lsb(a)));
    }
}

void Position::generate_legal(MoveList& list) {
    list.count = 0;

    MoveList pseudo;
    generate_pseudo(*this, pseudo);

    Color us = sideToMove;
    for (Move m : pseudo) {
        do_move(m);
        // After do_move, sideToMove is the opponent; the move is legal iff the
        // side that just moved did not leave its own king attacked.
        bool legal = (attackers_to(king_square(us)) & pieces(sideToMove)) == 0;
        undo_move(m);
        if (legal) list.add(m);
    }

    generate_castling(*this, list); // already fully legal
}

} // namespace chess
