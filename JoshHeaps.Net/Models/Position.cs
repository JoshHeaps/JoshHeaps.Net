
namespace JoshHeaps.Net.Models;

public struct Position(int row, int col)
{
    public int Row { get; set; } = row;
    public int Col { get; set; } = col;

    public override readonly string ToString()
    {
        return $"[{Row}, {Col}]";
    }

    internal readonly void Deconstruct(out int row, out int col)
    {
        row = Row; col = Col;
    }
}
