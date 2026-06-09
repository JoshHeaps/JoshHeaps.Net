#include "position.h"
#include "zobrist.h"

#include <cctype>
#include <cstdio>
#include <cstring>
#include <memory>


namespace chess {

namespace {

// Bits of castling rights that are revoked when a piece leaves/arrives a square
// (covers king moves, rook moves, and rook captures uniformly).
int castling_mask(Square s) {
    switch (s) {
        case E1: return WHITE_OO | WHITE_OOO;
        case A1: return WHITE_OOO;
        case H1: return WHITE_OO;
        case E8: return BLACK_OO | BLACK_OOO;
        case A8: return BLACK_OOO;
        case H8: return BLACK_OO;
        default: return 0;
    }
}

char piece_to_char(Piece p) {
    const char* w = " PNBRQK";
    char c = w[type_of(p)];
    return color_of(p) == BLACK ? char(std::tolower(c)) : c;
}

} // namespace

void Position::put_piece(Piece pc, Square s) {
    board[s] = pc;
    byTypeBB[type_of(pc)] |= square_bb(s);
    byColorBB[color_of(pc)] |= square_bb(s);
    zkey ^= Zobrist::psq[pc][s];
}

void Position::remove_piece(Square s) {
    Piece pc = board[s];
    byTypeBB[type_of(pc)] ^= square_bb(s);
    byColorBB[color_of(pc)] ^= square_bb(s);
    board[s] = NO_PIECE;
    zkey ^= Zobrist::psq[pc][s];
}

void Position::move_piece(Square from, Square to) {
    Piece pc = board[from];
    Bitboard fromTo = square_bb(from) | square_bb(to);
    byTypeBB[type_of(pc)] ^= fromTo;
    byColorBB[color_of(pc)] ^= fromTo;
    board[from] = NO_PIECE;
    board[to] = pc;
    zkey ^= Zobrist::psq[pc][from] ^ Zobrist::psq[pc][to];
}

bool Position::can_castle(Color c, CastlingSide side) const {
    int r = (c == WHITE) ? (side == KINGSIDE ? WHITE_OO : WHITE_OOO)
                         : (side == KINGSIDE ? BLACK_OO : BLACK_OOO);
    return (castlingRights & r) != 0;
}

Position Position::from_fen(std::string_view fen) {
    auto held = std::make_unique<Position>();
    Position& p = *held;
    std::memset(p.byTypeBB, 0, sizeof(p.byTypeBB));
    std::memset(p.byColorBB, 0, sizeof(p.byColorBB));
    for (int s = 0; s < SQUARE_NB; ++s) p.board[s] = NO_PIECE;
    p.sideToMove = WHITE;
    p.castlingRights = NO_CASTLING;
    p.epSquare = SQ_NONE;
    p.rule50 = 0;
    p.gamePly = 0;
    p.zkey = 0;
    p.undoCount = 0;

    size_t i = 0;
    int rank = 7, file = 0;

    // 1) piece placement
    for (; i < fen.size() && fen[i] != ' '; ++i) {
        char c = fen[i];
        if (c == '/') { --rank; file = 0; }
        else if (std::isdigit((unsigned char)c)) { file += c - '0'; }
        else {
            Color col = std::isupper((unsigned char)c) ? WHITE : BLACK;
            PieceType pt = NO_PIECE_TYPE;
            switch (std::tolower((unsigned char)c)) {
                case 'p': pt = PAWN; break;
                case 'n': pt = KNIGHT; break;
                case 'b': pt = BISHOP; break;
                case 'r': pt = ROOK; break;
                case 'q': pt = QUEEN; break;
                case 'k': pt = KING; break;
            }
            if (pt != NO_PIECE_TYPE)
                p.put_piece(make_piece(col, pt), make_square(File(file), Rank(rank)));
            ++file;
        }
    }

    auto skip_space = [&] { while (i < fen.size() && fen[i] == ' ') ++i; };

    // 2) side to move
    skip_space();
    if (i < fen.size()) { p.sideToMove = (fen[i] == 'b') ? BLACK : WHITE; ++i; }

    // 3) castling rights
    skip_space();
    for (; i < fen.size() && fen[i] != ' '; ++i) {
        switch (fen[i]) {
            case 'K': p.castlingRights |= WHITE_OO; break;
            case 'Q': p.castlingRights |= WHITE_OOO; break;
            case 'k': p.castlingRights |= BLACK_OO; break;
            case 'q': p.castlingRights |= BLACK_OOO; break;
            default: break; // '-' or Chess960 letters
        }
    }

    // 4) en passant
    skip_space();
    if (i < fen.size() && fen[i] != '-' && fen[i] != ' ') {
        File f = File(fen[i] - 'a');
        Rank r = Rank(fen[i + 1] - '1');
        p.epSquare = make_square(f, r);
        i += 2;
    } else if (i < fen.size() && fen[i] == '-') {
        ++i;
    }

    // 5) halfmove clock
    skip_space();
    int halfmove = 0;
    for (; i < fen.size() && std::isdigit((unsigned char)fen[i]); ++i)
        halfmove = halfmove * 10 + (fen[i] - '0');
    p.rule50 = halfmove;

    // 6) fullmove number
    skip_space();
    int fullmove = 1;
    if (i < fen.size() && std::isdigit((unsigned char)fen[i])) {
        fullmove = 0;
        for (; i < fen.size() && std::isdigit((unsigned char)fen[i]); ++i)
            fullmove = fullmove * 10 + (fen[i] - '0');
    }
    p.gamePly = (fullmove - 1) * 2 + (p.sideToMove == BLACK ? 1 : 0);

    // finalize the hash
    if (p.sideToMove == BLACK) p.zkey ^= Zobrist::side;
    p.zkey ^= Zobrist::castling[p.castlingRights];
    if (p.epSquare != SQ_NONE) p.zkey ^= Zobrist::enpassant[file_of(p.epSquare)];

    p.repKeys[0] = p.zkey;
    p.repCount = 1;
    return p;
}

std::string Position::to_fen() const {
    std::string s;
    for (int r = 7; r >= 0; --r) {
        int empty = 0;
        for (int f = 0; f < 8; ++f) {
            Piece pc = board[make_square(File(f), Rank(r))];
            if (pc == NO_PIECE) { ++empty; continue; }
            if (empty) { s += char('0' + empty); empty = 0; }
            s += piece_to_char(pc);
        }
        if (empty) s += char('0' + empty);
        if (r) s += '/';
    }
    s += sideToMove == WHITE ? " w " : " b ";

    std::string cr;
    if (castlingRights & WHITE_OO)  cr += 'K';
    if (castlingRights & WHITE_OOO) cr += 'Q';
    if (castlingRights & BLACK_OO)  cr += 'k';
    if (castlingRights & BLACK_OOO) cr += 'q';
    s += cr.empty() ? "-" : cr;

    s += ' ';
    if (epSquare == SQ_NONE) s += '-';
    else { s += char('a' + file_of(epSquare)); s += char('1' + rank_of(epSquare)); }

    s += ' ';
    s += std::to_string(rule50);
    s += ' ';
    s += std::to_string(fullmove_number());
    return s;
}

Bitboard Position::attackers_to(Square s, Bitboard occ) const {
    return (PawnAttacks[BLACK][s] & pieces(WHITE, PAWN))
         | (PawnAttacks[WHITE][s] & pieces(BLACK, PAWN))
         | (KnightAttacks[s] & byTypeBB[KNIGHT])
         | (KingAttacks[s] & byTypeBB[KING])
         | (bishop_attacks(s, occ) & (byTypeBB[BISHOP] | byTypeBB[QUEEN]))
         | (rook_attacks(s, occ) & (byTypeBB[ROOK] | byTypeBB[QUEEN]));
}

bool Position::in_check() const {
    return (attackers_to(king_square(sideToMove)) & pieces(~sideToMove)) != 0;
}

bool Position::gives_check(Move m) {
    do_move(m);
    bool checked = in_check();
    undo_move(m);
    return checked;
}

void Position::do_move(Move m) {
    Color us = sideToMove, them = ~us;
    Square from = m.from(), to = m.to();
    MoveFlag flag = m.type();
    Piece pc = board[from];
    Piece captured = (flag == EN_PASSANT) ? make_piece(them, PAWN) : board[to];

    Undo& u = undoStack[undoCount++];
    u.castlingRights = castlingRights;
    u.epSquare = epSquare;
    u.rule50 = rule50;
    u.key = zkey;
    u.captured = captured;

    if (epSquare != SQ_NONE) {
        zkey ^= Zobrist::enpassant[file_of(epSquare)];
        epSquare = SQ_NONE;
    }

    ++rule50;

    if (captured != NO_PIECE) {
        Square capsq = to;
        if (flag == EN_PASSANT) capsq = (us == WHITE) ? Square(to - 8) : Square(to + 8);
        remove_piece(capsq);
        rule50 = 0;
    }

    move_piece(from, to);

    if (type_of(pc) == PAWN) {
        rule50 = 0;
        if ((int(to) ^ int(from)) == 16) {
            epSquare = Square((from + to) / 2);
            zkey ^= Zobrist::enpassant[file_of(epSquare)];
        } else if (flag == PROMOTION) {
            remove_piece(to);
            put_piece(make_piece(us, m.promotion()), to);
        }
    }

    if (flag == CASTLING) {
        Square rookFrom, rookTo;
        if (to > from) { rookFrom = Square(from + 3); rookTo = Square(from + 1); }
        else           { rookFrom = Square(from - 4); rookTo = Square(from - 1); }
        move_piece(rookFrom, rookTo);
    }

    int cr = castlingRights & ~(castling_mask(from) | castling_mask(to));
    if (cr != castlingRights) {
        zkey ^= Zobrist::castling[castlingRights];
        zkey ^= Zobrist::castling[cr];
        castlingRights = cr;
    }

    sideToMove = them;
    zkey ^= Zobrist::side;
    ++gamePly;

    repKeys[repCount++] = zkey;
}

void Position::undo_move(Move m) {
    Color us = ~sideToMove;
    Square from = m.from(), to = m.to();
    MoveFlag flag = m.type();
    Undo u = undoStack[--undoCount];

    if (flag == PROMOTION) {
        remove_piece(to);
        put_piece(make_piece(us, PAWN), to);
    }

    move_piece(to, from);

    if (u.captured != NO_PIECE) {
        Square capsq = to;
        if (flag == EN_PASSANT) capsq = (us == WHITE) ? Square(to - 8) : Square(to + 8);
        put_piece(u.captured, capsq);
    }

    if (flag == CASTLING) {
        Square rookFrom, rookTo;
        if (to > from) { rookFrom = Square(from + 3); rookTo = Square(from + 1); }
        else           { rookFrom = Square(from - 4); rookTo = Square(from - 1); }
        move_piece(rookTo, rookFrom);
    }

    sideToMove = us;
    castlingRights = u.castlingRights;
    epSquare = u.epSquare;
    rule50 = u.rule50;
    zkey = u.key;
    --gamePly;
    --repCount;
}

bool Position::insufficient_material() const {
    if (byTypeBB[PAWN] | byTypeBB[ROOK] | byTypeBB[QUEEN])
        return false;
    int minors = popcount(byTypeBB[KNIGHT] | byTypeBB[BISHOP]);
    return minors <= 1; // KvK, KvKN, KvKB
}

void Position::seed_history(const uint64_t* priorKeys, int count) {
    if (count <= 0) return;
    if (count > 1000) count = 1000;          // leave headroom in repKeys for search plies

    uint64_t current = zkey;                  // from_fen placed this at repKeys[0]
    for (int i = 0; i < count; ++i)
        repKeys[i] = priorKeys[i];
    repKeys[count] = current;
    repCount = count + 1;
    rule50   = count;                         // == half-moves since the last irreversible move
}

bool Position::is_draw() const {
    if (rule50 >= 100) return true;
    if (insufficient_material()) return true;

    uint64_t k = repKeys[repCount - 1];
    int seen = 0;
    for (int i = repCount - 3; i >= 0 && i >= repCount - 1 - rule50; i -= 2)
        if (repKeys[i] == k && ++seen >= 2)
            return true; // threefold
    return false;
}

void Position::print() const {
    std::printf("\n  +---+---+---+---+---+---+---+---+\n");
    for (int r = 7; r >= 0; --r) {
        std::printf("%d ", r + 1);
        for (int f = 0; f < 8; ++f) {
            Piece pc = board[make_square(File(f), Rank(r))];
            std::printf("| %c ", pc == NO_PIECE ? ' ' : piece_to_char(pc));
        }
        std::printf("|\n  +---+---+---+---+---+---+---+---+\n");
    }
    std::printf("    a   b   c   d   e   f   g   h\n");
    std::printf("  %s to move   key=%016llx\n",
                sideToMove == WHITE ? "White" : "Black",
                (unsigned long long)zkey);
}

} // namespace chess
