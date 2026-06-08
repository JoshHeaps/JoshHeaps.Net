namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Creates <see cref="IChessEngine"/> instances, choosing the implementation from configuration.
/// </summary>
public interface IChessEngineFactory
{
    /// <summary>
    /// Creates a new engine instance for a single game. The caller owns and disposes it.
    /// </summary>
    /// <param name="skill">The desired playing strength / search depth.</param>
    /// <returns>A new, owned <see cref="IChessEngine"/>.</returns>
    IChessEngine Create(int skill);
}
