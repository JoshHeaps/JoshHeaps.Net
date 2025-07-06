using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

public interface IChessService
{
    GameState CreateNewGame();
    List<(ChessPiece piece, List<Position> moves)> GetAllLegalMoves(GameState gameState);
    List<Position> GetLegalMovesForPiece(GameState gameState, string pieceId);
    Task<MoveResultDto> MakeMove(GameState gameState, MoveDto moveDto);
}
