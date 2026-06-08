namespace JoshHeaps.Net.Models;

public class MoveResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; }
    public bool IsCheck { get; set; }
    public bool IsCheckmate { get; set; }
    public bool IsStalemate { get; set; }
    public bool IsThreefoldRepetition { get; set; }
}
