using JoshHeaps.Net.Services.Implementations;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>Per-side engine and strength for a CPU-vs-CPU game.</summary>
public sealed record SelfPlayConfig(
    ChessEngineKind WhiteKind, int WhiteSkill,
    ChessEngineKind BlackKind, int BlackSkill);

/// <summary>
/// Creates and runs CPU-vs-CPU games to completion: randomized opening, move loop, and (when
/// the learned engine plays) feeding the result back into the learned weights. Used by the
/// spectator "watch" endpoint and by the auto-trainer.
/// </summary>
public interface ISelfPlayCoordinator
{
    /// <summary>
    /// Create, register, and start running a self-play game. Returns immediately with the new
    /// game's id and a task that completes when the game finishes (or is cancelled). Callers
    /// that only need the id can ignore the task; the auto-trainer awaits it to start the next.
    /// </summary>
    (Guid GameId, Task Completion) StartGame(SelfPlayConfig config, CancellationToken cancellationToken = default);
}
