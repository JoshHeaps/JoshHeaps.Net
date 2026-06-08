using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Engine-agnostic glue for computer moves: get a move from the engine, apply it
/// through the rules service, and broadcast it to the game's clients.
/// </summary>
public sealed class ComputerMoveOrchestrator(
    IHubContext<ChessHub> chessHub,
    IChessService chessService) : IComputerMoveOrchestrator
{
    public async Task<(MoveDto move, MoveResultDto result)> PlayAsync(GameState state, IChessEngine engine)
    {
        var uci = await engine.GetBestMoveAsync(state.ToFen());

        var move = uci.ToMoveDto(
            state,
            state.CurrentPlayer == PieceColor.White
                ? state.WhitePlayerId
                : state.BlackPlayerId);

        var result = chessService.MakeMove(state, move);

        await chessHub.Clients.Group(state.GameId.ToString())
            .SendAsync("ReceiveMoveUpdate", state.GameId.ToString(), move, result);

        return (move, result);
    }
}
