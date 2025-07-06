using JoshHeaps.Net.DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace JoshHeaps.Net.Services.Implementations;

public sealed class GameCleanupService(
        IDbContextFactory<ChessDbContext> dbFactory,
        ILogger<GameCleanupService> log)
    : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stop);

                var now = DateTime.UtcNow;

                var expired = await db.Games
                    .Where(g => g.LastMoveUtc.AddDays(7) < DateTime.UtcNow)
                    .ToListAsync(stop);

                if (expired.Count == 0) continue;

                db.Games.RemoveRange(expired);
                await db.SaveChangesAsync(stop);

                log.LogInformation("🗑️  Removed {Count} expired games", expired.Count);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                log.LogError(ex, "Error while purging old games");
            }
        }
    }
}
