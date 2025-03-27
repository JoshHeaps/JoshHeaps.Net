namespace JoshHeaps.Net.Models;

public class MoveDto
{
    public Guid GameId { get; set; }
    public Guid PlayerId { get; set; }

    public string PieceId { get; set; }

    // The square the piece is moving from
    public int SourceRow { get; set; }
    public int SourceCol { get; set; }

    // The square the piece is moving to
    public int TargetRow { get; set; }
    public int TargetCol { get; set; }

    // If this is a pawn promotion, specify the piece type to promote to; otherwise null
    public PieceType? PromotionChoice { get; set; }
}