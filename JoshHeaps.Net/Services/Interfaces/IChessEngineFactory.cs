using JoshHeaps.Net.Services.Implementations;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Creates <see cref="IChessEngine"/> instances, choosing the implementation from configuration.
/// </summary>
public interface IChessEngineFactory
{
    /// <summary>
    /// Creates a new engine instance for a single game using the configured default engine.
    /// The caller owns and disposes it.
    /// </summary>
    /// <param name="skill">The desired playing strength / search depth.</param>
    /// <returns>A new, owned <see cref="IChessEngine"/>.</returns>
    IChessEngine Create(int skill);

    /// <summary>
    /// Creates a new engine instance for a single game using an explicitly chosen engine
    /// (e.g. for picking a different engine per side in a CPU-vs-CPU game). The caller owns
    /// and disposes it.
    /// </summary>
    /// <param name="skill">The desired playing strength / search depth.</param>
    /// <param name="kind">The engine implementation to create.</param>
    /// <returns>A new, owned <see cref="IChessEngine"/>.</returns>
    IChessEngine Create(int skill, ChessEngineKind kind);
}
