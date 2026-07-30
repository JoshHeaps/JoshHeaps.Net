using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Process-wide registry of ranging rooms. Holds only device identity and the sample indices each
/// device reported, so the server is a scheduler and a relay — it never sees audio.
/// </summary>
public interface IEchoRoomStore
{
    /// <summary>Add a device to a room, creating the room if this is the first arrival.</summary>
    EchoJoinResult Join(string roomCode, string connectionId, string displayName, int sampleRate);

    /// <summary>Remove whichever device owns this connection, returning the room it left.</summary>
    (string? roomCode, EchoRoomSnapshot? room) Leave(string connectionId);

    /// <summary>Snapshot of a room's roster, or null if the room is gone.</summary>
    EchoRoomSnapshot? Snapshot(string roomCode);

    /// <summary>Room codes with at least two devices, which is the minimum for a measurement.</summary>
    IReadOnlyCollection<string> MeasurableRooms { get; }

    /// <summary>
    /// Open a new round for a room, rotating which device chirps first so no single device is
    /// permanently the slot-order anchor.
    /// </summary>
    EchoRoundSchedule? StartRound(
        string roomCode,
        int slotMilliseconds,
        int tailMilliseconds,
        TimeSpan lead,
        TimeSpan grace);

    /// <summary>File a device's peaks against the room's open round.</summary>
    bool Report(string connectionId, EchoPeakReport report);

    /// <summary>
    /// Close the open round if every device has reported or its deadline has passed, returning the
    /// reports to broadcast.
    /// </summary>
    EchoRoundResult? TryCloseRound(string roomCode);

    /// <summary>Drop rooms that have had no activity for longer than <paramref name="idleFor"/>.</summary>
    int PruneIdle(TimeSpan idleFor);
}
