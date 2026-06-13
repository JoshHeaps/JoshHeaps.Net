using JoshHeaps.Net.Services.Implementations;
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
    public bool IsThreefoldRepetition { get; set; }
    public bool IsForfeited { get; set; } = false;

    // The color that won, when the game ended by forfeit (null while the game is live).
    public PieceColor? Winner { get; set; }

    // Keep a history of moves if desired
    public List<string> MoveHistory { get; set; }

    // Position keys (FEN placement/side/castling/en-passant) for threefold-repetition detection.
    public List<string> PositionHistory { get; set; }

    // Moves in standard algebraic notation (SAN), in order, for PGN export.
    public List<string> SanHistory { get; set; }

    // Half-moves since the last capture or pawn move (the FEN 50-move clock). Also tells
    // us how many trailing PositionHistory entries belong to the current repetition window.
    public int HalfmoveClock { get; set; }

    // A list of all pieces to quickly reference them (optional but convenient).
    // Alternatively, you can iterate the Board array.
    public List<ChessPiece> Pieces { get; set; }

    public bool WhiteJoined { get; set; } = false;
    public bool BlackJoined { get; set; } = false;
    public Guid WhitePlayerId { get; set; }
    public Guid BlackPlayerId { get; set; }

    public bool IsVsComputer { get; set; } = false;
    public bool IsComputerVsComputer { get; set; } = false;

    // The engine playing each side (null for a human). In a human-vs-computer game only the
    // computer's side is set; the orchestrator picks the engine for whoever is to move.
    public IChessEngine? WhiteComputer { get; set; }
    public IChessEngine? BlackComputer { get; set; }

    // Which engine implementation each side uses — lets game-over handling know which side(s)
    // are the learning engine, and lets spectators see who is playing.
    public ChessEngineKind WhiteEngineKind { get; set; }
    public ChessEngineKind BlackEngineKind { get; set; }

    // Native per-game training accumulator (nint.Zero when this game isn't training the
    // learned engine). The engine records each played position into it and applies the
    // result on game over.
    public nint Trainer { get; set; }

    // optional: convenience
    public bool IsOpen => !WhiteJoined || !BlackJoined;

    public GameState()
    {
        GameId = Guid.NewGuid();
        Board = new ChessPiece[8, 8];
        Pieces = [];
        MoveHistory = [];
        PositionHistory = [];
        SanHistory = [];
    }
}
