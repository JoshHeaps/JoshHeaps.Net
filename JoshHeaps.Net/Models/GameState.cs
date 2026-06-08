using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Models;

public class GameState
{
    public Guid GameId { get; set; }

    // 8x8 board of references. Null if no piece is present.
    // row 0 at top -> row 7 at bottom (typical 0-based array).
    public ChessPiece?[,] Board { get; set; }

    // Whose turn is it?
    public PieceColor CurrentPlayer { get; set; }

    // To detect if en passant is possible, store the position of a pawn that just moved two squares.
    // If no pawn is currently "en-passant capturable," this could be null.
    public Position? EnPassantTarget { get; set; }

    // Castling rights: have the rooks or king moved?
    // In real chess notation, you track it per side, e.g. "KQkq" style. Let’s store boolean flags:
    public bool WhiteCanCastleKingside { get; set; }
    public bool WhiteCanCastleQueenside { get; set; }
    public bool BlackCanCastleKingside { get; set; }
    public bool BlackCanCastleQueenside { get; set; }

    // Some game status
    public bool IsCheck { get; set; }
    public bool IsCheckmate { get; set; }
    public bool IsStalemate { get; set; }

    // Keep a history of moves if desired
    public List<string> MoveHistory { get; set; }

    // A list of all pieces to quickly reference them (optional but convenient).
    // Alternatively, you can iterate the Board array.
    public List<ChessPiece> Pieces { get; set; }

    public bool WhiteJoined { get; set; } = false;
    public bool BlackJoined { get; set; } = false;
    public Guid WhitePlayerId { get; set; }
    public Guid BlackPlayerId { get; set; }

    public bool IsVsComputer { get; set; } = false;

    public IChessEngine? Computer { get; set; }

    // optional: convenience
    public bool IsOpen => !WhiteJoined || !BlackJoined;

    public GameState()
    {
        GameId = Guid.NewGuid();
        Board = new ChessPiece[8, 8];
        Pieces = [];
        MoveHistory = [];
    }
}
