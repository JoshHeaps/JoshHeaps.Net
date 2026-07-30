using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Hubs;

/// <summary>
/// Membership and peak reporting for acoustic ranging rooms. Nothing that arrives here is audio:
/// a report is a handful of sample indices, and the geometry is solved on the clients.
/// </summary>
public class EchoHub(IEchoRoomStore rooms) : Hub
{
    /// <summary>Join (or create) a room and receive an id plus the current roster.</summary>
    public async Task<EchoJoinResult> JoinRoom(string roomCode, string displayName, int sampleRate)
    {
        var result = rooms.Join(roomCode, Context.ConnectionId, displayName, sampleRate);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(result.RoomCode));
        await Clients.Group(GroupFor(result.RoomCode)).SendAsync("RoomChanged", result.Room);

        return result;
    }

    /// <summary>File this device's arrival indices for the room's open round.</summary>
    public bool ReportRound(EchoPeakReport report) => rooms.Report(Context.ConnectionId, report);

    /// <summary>
    /// Server clock, for estimating each device's offset from it. Only needs to be good to a few
    /// tens of milliseconds: it decides which chirp is whose, never how far away anything is.
    /// </summary>
    public long ServerTime() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public async Task<bool> LeaveRoom() => await RemoveFromRoom();

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await RemoveFromRoom();
        await base.OnDisconnectedAsync(exception);
    }

    internal static string GroupFor(string roomCode) => $"echo:{roomCode.ToUpperInvariant()}";

    private async Task<bool> RemoveFromRoom()
    {
        var (roomCode, room) = rooms.Leave(Context.ConnectionId);
        if (roomCode is null) return false;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(roomCode));
        if (room is not null) await Clients.Group(GroupFor(roomCode)).SendAsync("RoomChanged", room);

        return true;
    }
}
