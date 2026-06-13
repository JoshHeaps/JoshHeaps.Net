using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Continuously trains the learned engine in the background: two self-play games run in
/// parallel, the learned engine (skill 6) against Stockfish (skill 20), one with Stockfish as
/// black and one as white. Each slot is independent — when its game finishes it immediately
/// starts another under the same conditions, without waiting on the other slot. Registered
/// only outside Development and gated by the ChessEngine:AutoTrain config flag.
/// </summary>
public sealed class AutoTrainingService(
    ISelfPlayCoordinator coordinator,
    ILogger<AutoTrainingService> logger) : BackgroundService
{
    private const int LearnedSkill = 6;
    private const int StockfishSkill = 20;
    private static readonly TimeSpan _restartBackoff = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Learned plays both colors so the model trains symmetrically; each slot is its own loop.
        var stockfishBlack = RunSlot(
            new SelfPlayConfig(ChessEngineKind.CustomLearned, LearnedSkill, ChessEngineKind.Stockfish, StockfishSkill),
            stoppingToken);
        var stockfishWhite = RunSlot(
            new SelfPlayConfig(ChessEngineKind.Stockfish, StockfishSkill, ChessEngineKind.CustomLearned, LearnedSkill),
            stoppingToken);

        return Task.WhenAll(stockfishBlack, stockfishWhite);
    }

    private async Task RunSlot(SelfPlayConfig config, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.StartGame(config, stoppingToken).Completion;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Most likely an engine failing to start (e.g. Stockfish). Back off so a
                // persistent failure doesn't spin a tight loop, then try again.
                logger.LogError(ex, "Auto-training game failed to start; retrying after backoff.");
                try { await Task.Delay(_restartBackoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
