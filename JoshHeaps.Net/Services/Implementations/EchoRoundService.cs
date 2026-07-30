using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Services.Implementations;

public sealed class EchoRoundSettings
{
    public const string SectionName = "Echo";

    /// <summary>
    /// Slot length. Wide enough that no device's chirp can land in another's search window once
    /// unknown output latency (tens of milliseconds, different per device) and message jitter are
    /// accounted for.
    /// </summary>
    public int SlotMilliseconds { get; set; } = 500;

    /// <summary>Time after the last chirp for propagation, detection and reporting.</summary>
    public int TailMilliseconds { get; set; } = 700;

    public int GraceMilliseconds { get; set; } = 1500;
    public int GapMilliseconds { get; set; } = 250;
    public int IdleRoomMinutes { get; set; } = 10;

    /// <summary>
    /// How far ahead a round is announced. Must exceed the worst message delivery plus client
    /// stall, or a device will find its slot already gone and sit the round out.
    /// </summary>
    public int LeadMilliseconds { get; set; } = 600;
}

/// <summary>
/// Drives continuous ranging: opens a round for every room that has two or more devices, closes it
/// once everyone has reported or the deadline passes, and broadcasts the raw peak table.
/// </summary>
public sealed class EchoRoundService(
    IEchoRoomStore rooms,
    IHubContext<EchoHub> hub,
    EchoRoundSettings settings,
    ILogger<EchoRoundService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _nextRoundAllowedAt = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPrune = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync();
                lastPrune = PruneIfDue(lastPrune);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Echo round tick failed");
            }

            await Task.Delay(50, stoppingToken);
        }
    }

    private async Task TickAsync()
    {
        foreach (var roomCode in rooms.MeasurableRooms)
        {
            await CloseFinishedRoundAsync(roomCode);
            await OpenRoundIfDueAsync(roomCode);
        }
    }

    private async Task CloseFinishedRoundAsync(string roomCode)
    {
        var result = rooms.TryCloseRound(roomCode);
        if (result is null) return;

        _nextRoundAllowedAt[roomCode] = DateTimeOffset.UtcNow.AddMilliseconds(settings.GapMilliseconds);
        await hub.Clients.Group(EchoHub.GroupFor(roomCode)).SendAsync("RoundComplete", result);
    }

    private async Task OpenRoundIfDueAsync(string roomCode)
    {
        if (_nextRoundAllowedAt.TryGetValue(roomCode, out var earliest) && DateTimeOffset.UtcNow < earliest) return;

        var schedule = rooms.StartRound(
            roomCode,
            settings.SlotMilliseconds,
            settings.TailMilliseconds,
            TimeSpan.FromMilliseconds(settings.LeadMilliseconds),
            TimeSpan.FromMilliseconds(settings.GraceMilliseconds));

        if (schedule is null) return;

        await hub.Clients.Group(EchoHub.GroupFor(roomCode)).SendAsync("RoundStarting", schedule);
    }

    private DateTimeOffset PruneIfDue(DateTimeOffset lastPrune)
    {
        if (DateTimeOffset.UtcNow - lastPrune < TimeSpan.FromMinutes(1)) return lastPrune;

        var pruned = rooms.PruneIdle(TimeSpan.FromMinutes(settings.IdleRoomMinutes));
        if (pruned > 0) logger.LogInformation("Pruned {Count} idle echo room(s)", pruned);

        return DateTimeOffset.UtcNow;
    }
}
