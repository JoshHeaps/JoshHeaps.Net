namespace JoshHeaps.Net.Models;

public class ChessPiece(string id, PieceType type, PieceColor color, Position position)
{
    public string Id { get; set; } = id;
    public PieceType Type { get; set; } = type;
    public PieceColor Color { get; set; } = color;
    public Position Position { get; set; } = position;

    public bool HasMoved { get; set; } = false;
}