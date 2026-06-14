using System.Collections.Concurrent;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// In-memory game registry with a delayed-removal lifecycle. Singleton: the game state is
/// process-wide, not per-request, so it lives in a service rather than static controller fields.
/// </summary>
public sealed class GameStore(ILearnedWeightsStore weightsStore) : IGameStore
{
    private readonly ConcurrentDictionary<Guid, GameState> _games = [];
    private readonly ConcurrentDictionary<Guid, Task> _removalTasks = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _removalCts = [];

    public void Add(GameState game) => _games[game.GameId] = game;

    public bool TryGet(Guid id, out GameState game) => _games.TryGetValue(id, out game!);

    public bool Contains(Guid id) => _games.ContainsKey(id);

    public IReadOnlyCollection<GameState> All => [.. _games.Values];

    public void ScheduleRemove(Guid id, TimeSpan delay)
    {
        if (_removalCts.TryRemove(id, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _removalCts[id] = cts;

        _removalTasks[id] = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);

                if (_games.TryGetValue(id, out var game))
                {
                    if (game.WhiteComputer is not null)
                        await game.WhiteComputer.DisposeAsync();
                    if (game.BlackComputer is not null)
                        await game.BlackComputer.DisposeAsync();

                    // Free the trainer if the game never reached ApplyLearning (e.g. timed out).
                    if (game.Trainer != nint.Zero)
                    {
                        weightsStore.DestroyTrainer(game.Trainer);
                        game.Trainer = nint.Zero;
                    }
                }

                _games.Remove(id, out _);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_removalCts.TryGetValue(id, out var currentCts) && currentCts == cts)
                    _removalCts.TryRemove(id, out _);

                cts.Dispose();
            }
        });
    }
}
