using Microsoft.Extensions.Options;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Runtime-adjustable auto-training settings. Singleton so the value set from the website (via the
/// chess controller) is seen live by the background <see cref="AutoTrainingService"/>. Seeded from
/// <see cref="ChessEngineOptions.AutoTrainGameCount"/> and clamped to a sane range.
/// </summary>
public sealed class AutoTrainingSettings
{
    /// <summary>Upper bound on concurrent auto-training games (each spawns a Stockfish + a learned engine).</summary>
    public const int MaxGames = 16;

    private int _gameCount;

    public AutoTrainingSettings(IOptions<ChessEngineOptions> options)
        => _gameCount = Clamp(options.Value.AutoTrainGameCount);

    /// <summary>
    /// Number of auto-training games to keep running concurrently. 0 pauses auto-training.
    /// Reads/writes are atomic; the background service reads this every cycle.
    /// </summary>
    public int GameCount
    {
        get => Volatile.Read(ref _gameCount);
        set => Volatile.Write(ref _gameCount, Clamp(value));
    }

    private static int Clamp(int n) => Math.Clamp(n, 0, MaxGames);
}
