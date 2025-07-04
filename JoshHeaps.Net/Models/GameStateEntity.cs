namespace JoshHeaps.Net.Models;

public class GameStateEntity
{
    public Guid GameId { get; set; }
    public string SerializedState { get; set; } = ""; // JSON
    public DateTime LastMoveUtc { get; set; }
}
