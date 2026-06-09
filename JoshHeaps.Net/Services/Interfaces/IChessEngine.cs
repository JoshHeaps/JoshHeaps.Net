namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// An AI move-selection engine (e.g. Stockfish or the custom native engine).
/// Selects a move for a position; it does not enforce rules or broadcast updates.
/// Implementations own unmanaged resources, hence <see cref="IAsyncDisposable"/>.
/// </summary>
public interface IChessEngine : IAsyncDisposable
{
    /// <summary>The engine's playing strength / search depth.</summary>
    int Skill { get; }

    /// <summary>
    /// Returns the engine's chosen move in UCI long-algebraic form (e.g. "e2e4", "e7e8q").
    /// </summary>
    /// <param name="fen">The current position as a FEN string.</param>
    /// <param name="historyFens">
    /// Prior positions since the last irreversible move, oldest first, excluding the
    /// current one — lets the engine detect threefold/50-move draws the FEN can't carry.
    /// May be empty.
    /// </param>
    /// <returns>The selected move as a UCI string.</returns>
    Task<string> GetBestMoveAsync(string fen, IReadOnlyList<string> historyFens);
}
