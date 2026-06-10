using System.Linq;

namespace JoshHeaps.Net.Models;

/// <summary>
/// A single piece as sent to the client. Captured pieces keep their original
/// type and color; their position is meaningless and omitted.
/// </summary>
public record ChessPieceDto(string Id, PieceType Type, PieceColor Color, int Row, int Col, bool HasMoved);

/// <summary>
/// The full board state pushed to clients. The same shape is returned by the
/// state endpoint, the move endpoint, and every SignalR move broadcast, so the
/// client always renders from one authoritative payload instead of re-fetching.
/// </summary>
public record GameStateDto(
    Guid GameId,
    string CurrentPlayer,
    bool IsCheck,
    bool IsCheckmate,
    bool IsStalemate,
    bool IsThreefoldRepetition,
    string? EnPassantTarget,
    bool WhiteCanCastleKingside,
    bool WhiteCanCastleQueenside,
    bool BlackCanCastleKingside,
    bool BlackCanCastleQueenside,
    IReadOnlyList<ChessPieceDto> Pieces,
    IReadOnlyList<ChessPieceDto> CapturedPieces,
    IReadOnlyList<string> MoveHistory,
    // Moves in standard algebraic notation, for the move-list panel.
    IReadOnlyList<string> SanHistory,
    // Monotonic ply counter the client uses to drop stale or echoed updates.
    int Version);

public static class GameStateMapper
{
    public static GameStateDto ToDto(this GameState gameState) => new(
        gameState.GameId,
        gameState.CurrentPlayer.ToString(),
        gameState.IsCheck,
        gameState.IsCheckmate,
        gameState.IsStalemate,
        gameState.IsThreefoldRepetition,
        gameState.EnPassantTarget?.ToString(),
        gameState.WhiteCanCastleKingside,
        gameState.WhiteCanCastleQueenside,
        gameState.BlackCanCastleKingside,
        gameState.BlackCanCastleQueenside,
        gameState.Pieces
            .Where(p => p.Position.Row >= 0)
            .Select(p => new ChessPieceDto(p.Id, p.Type, p.Color, p.Position.Row, p.Position.Col, p.HasMoved))
            .ToList(),
        gameState.Pieces
            .Where(p => p.Position.Row < 0)
            .Select(p => new ChessPieceDto(p.Id, p.Type, p.Color, p.Position.Row, p.Position.Col, p.HasMoved))
            .ToList(),
        gameState.MoveHistory,
        gameState.SanHistory,
        gameState.MoveHistory.Count);
}
