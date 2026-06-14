using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Continuously trains the learned engine in the background by keeping a configurable number of
/// self-play games running — the learned engine (skill 6) against Stockfish (skill 20), alternating
/// which color Stockfish takes so the model trains on both. The target count is read live from
/// <see cref="AutoTrainingSettings"/> (adjustable from the website): when a game finishes another
/// starts to refill the pool, raising the count starts more, and lowering it lets the surplus drain
/// as games finish (0 pauses training). Registered only outside Development and gated by the
/// ChessEngine:AutoTrain config flag.
/// </summary>
public sealed class AutoTrainingService(
    ISelfPlayCoordinator coordinator,
    AutoTrainingSettings settings,
    ILogger<AutoTrainingService> logger) : BackgroundService
{
    private const int LearnedSkill = 6;
    private const int StockfishSkill = 20;
    private static readonly TimeSpan _restartBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        int started = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);

            int desired = settings.GameCount;
            bool startFailed = false;

            while (running.Count < desired && !stoppingToken.IsCancellationRequested)
            {
                // Alternate Stockfish's color so the learned engine trains as both white and black.
                var config = started++ % 2 == 0
                    ? new SelfPlayConfig(ChessEngineKind.CustomLearned, LearnedSkill, ChessEngineKind.Stockfish, StockfishSkill)
                    : new SelfPlayConfig(ChessEngineKind.Stockfish, StockfishSkill, ChessEngineKind.CustomLearned, LearnedSkill);

                try
                {
                    var (_, completion) = coordinator.StartGame(config, stoppingToken);
                    running.Add(completion);
                }
                catch (Exception ex)
                {
                    // Most likely an engine failing to start (e.g. Stockfish). Back off so a
                    // persistent failure doesn't spin a tight loop, then try again.
                    logger.LogError(ex, "Failed to start an auto-training game; retrying after backoff.");
                    startFailed = true;
                    break;
                }
            }

            try
            {
                if (startFailed)
                    await Task.Delay(_restartBackoff, stoppingToken);
                else if (running.Count > 0)
                    // Wake when any game finishes (to refill) or after a short poll (to pick up a
                    // count increase promptly).
                    await Task.WhenAny(Task.WhenAny(running), Task.Delay(_pollInterval, stoppingToken));
                else
                    // Pool is empty (count is 0) — just poll for the count to change.
                    await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
