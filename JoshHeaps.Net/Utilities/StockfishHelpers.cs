using JoshHeaps.Net.Models;
using System.Text;

namespace JoshHeaps.Net.Utilities;

public static class StockfishHelpers
{
    public static MoveDto ToMoveDto(
        this string uci,
        GameState gameState,
        Guid playerId)
    {
        int fCol = uci[0] - 'a', fRow = 7 - (uci[1] - '1');
        int tCol = uci[2] - 'a', tRow = 7 - (uci[3] - '1');

        var piece = gameState.Board[fRow, fCol]
                    ?? throw new Exception("No piece at source square");

        PieceType? promo = uci.Length == 5 ? uci[4] switch
        {
            'q' => PieceType.Queen,
            'r' => PieceType.Rook,
            'b' => PieceType.Bishop,
            'n' => PieceType.Knight,
            _ => null
        } : null;

        return new MoveDto
        {
            GameId = gameState.GameId,
            PlayerId = playerId,
            PieceId = piece.Id,
            TargetRow = tRow,
            TargetCol = tCol,
            PromotionChoice = promo,
            SourceCol = fCol,
            SourceRow = fRow,
        };
    }

    /// <summary>
    /// Convert a 2-D board array (rank 8 = row 0, file a = col 0) to a FEN string.
    /// Only piece placement + active colour + castling are computed; the rest use
    /// safe defaults (-, 0, 1).  That is all Stockfish needs.
    /// </summary>
    public static string ToFen(this GameState gs)
    {
        var sb = new StringBuilder(64);

        /* 1) piece placement */
        for (int row = 0; row < 8; row++)
        {
            int empty = 0;

            for (int col = 0; col < 8; col++)
            {
                var p = gs.Board[row, col];

                if (p is null)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(ToFenChar(p));                 // ← unchanged helper
                }
            }

            if (empty > 0) sb.Append(empty);
            if (row < 7) sb.Append('/');
        }

        /* 2) active colour */
        sb.Append(gs.CurrentPlayer == PieceColor.White ? " w " : " b ");

        /* 3) castling rights (from GameState flags) */
        sb.Append(GetCastlingFlags(gs));

        /* 4) en-passant target square */
        sb.Append(' ');
        sb.Append(gs.EnPassantTarget.HasValue
            ? Alg(gs.EnPassantTarget.Value)
            : "-");

        /* 5-6) half-move clock + full-move number  */
        int fullMoves = gs.MoveHistory.Count / 2 + 1;
        sb.Append(" 0 ").Append(fullMoves);

        return sb.ToString();
    }

    /* ---------- helpers ---------- */

    private static string GetCastlingFlags(GameState gs)
    {
        var flags = new StringBuilder(4);

        if (gs.WhiteCanCastleKingside) flags.Append('K');
        if (gs.WhiteCanCastleQueenside) flags.Append('Q');
        if (gs.BlackCanCastleKingside) flags.Append('k');
        if (gs.BlackCanCastleQueenside) flags.Append('q');

        return flags.Length == 0 ? "-" : flags.ToString();
    }

    private static string Alg(Position p)
    {
        char file = (char)('a' + p.Col);
        int rank = 8 - p.Row;
        return $"{file}{rank}";
    }

    private static char ToFenChar(ChessPiece p) => p switch
    {
        { Type: PieceType.Pawn, Color: PieceColor.White } => 'P',
        { Type: PieceType.Pawn, Color: PieceColor.Black } => 'p',
        { Type: PieceType.Knight, Color: PieceColor.White } => 'N',
        { Type: PieceType.Knight, Color: PieceColor.Black } => 'n',
        { Type: PieceType.Bishop, Color: PieceColor.White } => 'B',
        { Type: PieceType.Bishop, Color: PieceColor.Black } => 'b',
        { Type: PieceType.Rook, Color: PieceColor.White } => 'R',
        { Type: PieceType.Rook, Color: PieceColor.Black } => 'r',
        { Type: PieceType.Queen, Color: PieceColor.White } => 'Q',
        { Type: PieceType.Queen, Color: PieceColor.Black } => 'q',
        { Type: PieceType.King, Color: PieceColor.White } => 'K',
        { Type: PieceType.King, Color: PieceColor.Black } => 'k',
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };
}
