namespace JoshHeaps.Net.Models;

/// <summary>A device taking part in a ranging room.</summary>
public sealed class EchoDevice
{
    public required string DeviceId { get; init; }
    public required string ConnectionId { get; init; }
    public required string DisplayName { get; set; }
    public int SampleRate { get; set; }
}

/// <summary>
/// One chirp cycle: every device plays in turn, and every device listens to the whole thing.
/// </summary>
public sealed class EchoRound
{
    public required string RoundId { get; init; }
    public required string[] SlotOrder { get; init; }
    public int SlotMilliseconds { get; init; }
    public DateTimeOffset Deadline { get; init; }
    public Dictionary<string, EchoPeakReport> Reports { get; } = [];
}

/// <summary>
/// What one device heard. Peaks are fractional sample indices into that device's own continuous
/// recording, one per slot, null where a chirp was not detected. Nothing else is ever uploaded.
/// </summary>
public sealed class EchoPeakReport
{
    public required string DeviceId { get; init; }
    public required string RoundId { get; init; }
    public int Slot { get; init; }
    public int SampleRate { get; init; }
    public double Epsilon { get; init; }
    public double?[] Peaks { get; init; } = [];
}

public sealed class EchoJoinResult
{
    public required string DeviceId { get; init; }
    public required string RoomCode { get; init; }
    public required EchoRoomSnapshot Room { get; init; }
}

public sealed class EchoRoomSnapshot
{
    public required string RoomCode { get; init; }
    public required EchoDeviceSnapshot[] Devices { get; init; }
}

public sealed class EchoDeviceSnapshot
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public int SampleRate { get; init; }
}

/// <summary>Tells every device when to chirp: its own slot index and how long a slot lasts.</summary>
public sealed class EchoRoundSchedule
{
    public required string RoundId { get; init; }
    public required string[] SlotOrder { get; init; }
    public int SlotMilliseconds { get; init; }
    public int TailMilliseconds { get; init; }

    /// <summary>
    /// When slot zero should sound, in server time, set far enough ahead that every device can
    /// receive the message and book the playback before it arrives. Devices schedule against this
    /// rather than against message arrival, so one slow client cannot drag its chirp into another
    /// device's slot and invalidate the round for everybody.
    /// </summary>
    public long StartsAtUnixMs { get; init; }
}

/// <summary>
/// Every device's peaks for one round, broadcast unchanged. Each client solves the geometry itself
/// so the server never needs the ranging maths.
/// </summary>
public sealed class EchoRoundResult
{
    public required string RoundId { get; init; }
    public required string[] SlotOrder { get; init; }
    public required EchoPeakReport[] Reports { get; init; }
}
