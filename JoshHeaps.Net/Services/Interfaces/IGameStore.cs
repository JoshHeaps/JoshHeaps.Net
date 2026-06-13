using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Process-wide registry of in-memory games and their cleanup lifecycle. Shared by the HTTP
/// controller (human and single-computer games) and the self-play coordinator (CPU-vs-CPU and
/// auto-training games), so every game is reachable from one place for lookup and spectating.
/// </summary>
public interface IGameStore
{
    /// <summary>Add (or replace) a game in the registry.</summary>
    void Add(GameState game);

    /// <summary>Look a game up by id.</summary>
    bool TryGet(Guid id, out GameState game);

    /// <summary>Whether a game with this id is still in the registry.</summary>
    bool Contains(Guid id);

    /// <summary>Snapshot of all games currently in the registry.</summary>
    IReadOnlyCollection<GameState> All { get; }

    /// <summary>
    /// Schedule removal of a game after <paramref name="delay"/>, cancelling any prior schedule
    /// for it. On removal the game's engines are disposed and any training accumulator freed.
    /// </summary>
    void ScheduleRemove(Guid id, TimeSpan delay);
}
