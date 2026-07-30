using System.Collections.Concurrent;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// In-memory ranging rooms. Singleton: rooms are process-wide and short-lived, and the site runs
/// as a single instance, so there is nothing to persist and no backplane to coordinate.
/// </summary>
public sealed class EchoRoomStore : IEchoRoomStore
{
    private sealed class Room
    {
        public required string Code { get; init; }
        public List<EchoDevice> Devices { get; } = [];
        public EchoRound? Round { get; set; }
        public int RoundCounter { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _roomsByConnection = [];

    public EchoJoinResult Join(string roomCode, string connectionId, string displayName, int sampleRate)
    {
        var room = _rooms.GetOrAdd(roomCode, code => new Room { Code = code });
        var device = new EchoDevice
        {
            DeviceId = Guid.NewGuid().ToString("N")[..8],
            ConnectionId = connectionId,
            DisplayName = displayName,
            SampleRate = sampleRate
        };

        lock (room)
        {
            room.Devices.RemoveAll(existing => existing.ConnectionId == connectionId);
            room.Devices.Add(device);
            room.LastActivity = DateTimeOffset.UtcNow;
            _roomsByConnection[connectionId] = room.Code;

            return new EchoJoinResult { DeviceId = device.DeviceId, RoomCode = room.Code, Room = SnapshotLocked(room) };
        }
    }

    public (string? roomCode, EchoRoomSnapshot? room) Leave(string connectionId)
    {
        if (!_roomsByConnection.TryRemove(connectionId, out var roomCode)) return (null, null);
        if (!_rooms.TryGetValue(roomCode, out var room)) return (roomCode, null);

        lock (room)
        {
            room.Devices.RemoveAll(device => device.ConnectionId == connectionId);
            room.Round = null;
            room.LastActivity = DateTimeOffset.UtcNow;

            if (room.Devices.Count == 0) _rooms.TryRemove(roomCode, out _);

            return (roomCode, SnapshotLocked(room));
        }
    }

    public EchoRoomSnapshot? Snapshot(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return null;

        lock (room) return SnapshotLocked(room);
    }

    public IReadOnlyCollection<string> MeasurableRooms =>
        [.. _rooms.Values.Where(room => room.Devices.Count >= 2).Select(room => room.Code)];

    public EchoRoundSchedule? StartRound(
        string roomCode,
        int slotMilliseconds,
        int tailMilliseconds,
        TimeSpan lead,
        TimeSpan grace)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return null;

        lock (room)
        {
            if (room.Round is not null || room.Devices.Count < 2) return null;

            var slotOrder = RotatedSlotOrder(room);
            var startsAt = DateTimeOffset.UtcNow + lead;
            var duration = TimeSpan.FromMilliseconds(slotOrder.Length * slotMilliseconds + tailMilliseconds);

            room.Round = new EchoRound
            {
                RoundId = Guid.NewGuid().ToString("N")[..12],
                SlotOrder = slotOrder,
                SlotMilliseconds = slotMilliseconds,
                Deadline = startsAt + duration + grace
            };
            room.LastActivity = DateTimeOffset.UtcNow;

            return new EchoRoundSchedule
            {
                RoundId = room.Round.RoundId,
                SlotOrder = slotOrder,
                SlotMilliseconds = slotMilliseconds,
                TailMilliseconds = tailMilliseconds,
                StartsAtUnixMs = startsAt.ToUnixTimeMilliseconds()
            };
        }
    }

    public bool Report(string connectionId, EchoPeakReport report)
    {
        var device = FindDevice(connectionId, out var room);
        if (device is null || room is null) return false;

        lock (room)
        {
            if (room.Round?.RoundId != report.RoundId) return false;
            if (!room.Round.SlotOrder.Contains(device.DeviceId)) return false;

            room.Round.Reports[device.DeviceId] = report;
            room.LastActivity = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public EchoRoundResult? TryCloseRound(string roomCode)
    {
        if (!_rooms.TryGetValue(roomCode, out var room)) return null;

        lock (room)
        {
            var round = room.Round;
            if (round is null) return null;

            var everyoneReported = round.SlotOrder.All(round.Reports.ContainsKey);
            if (!everyoneReported && DateTimeOffset.UtcNow < round.Deadline) return null;

            room.Round = null;

            return new EchoRoundResult
            {
                RoundId = round.RoundId,
                SlotOrder = round.SlotOrder,
                Reports = [.. round.SlotOrder.Where(round.Reports.ContainsKey).Select(id => round.Reports[id])]
            };
        }
    }

    public int PruneIdle(TimeSpan idleFor)
    {
        var cutoff = DateTimeOffset.UtcNow - idleFor;
        var stale = _rooms.Values.Where(room => room.LastActivity < cutoff).Select(room => room.Code).ToList();

        foreach (var code in stale) _rooms.TryRemove(code, out _);

        return stale.Count;
    }

    private static EchoRoomSnapshot SnapshotLocked(Room room) =>
        new()
        {
            RoomCode = room.Code,
            Devices =
            [
                .. room.Devices.Select(device => new EchoDeviceSnapshot
                {
                    DeviceId = device.DeviceId,
                    DisplayName = device.DisplayName,
                    SampleRate = device.SampleRate
                })
            ]
        };

    private static string[] RotatedSlotOrder(Room room)
    {
        var offset = room.RoundCounter++ % room.Devices.Count;
        return [.. room.Devices.Skip(offset).Concat(room.Devices.Take(offset)).Select(device => device.DeviceId)];
    }

    private EchoDevice? FindDevice(string connectionId, out Room? room)
    {
        room = null;
        if (!_roomsByConnection.TryGetValue(connectionId, out var roomCode)) return null;
        if (!_rooms.TryGetValue(roomCode, out room)) return null;

        lock (room) return room.Devices.FirstOrDefault(device => device.ConnectionId == connectionId);
    }
}
