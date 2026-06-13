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
    IChessService chessService,
    ILearnedWeightsStore weightsStore) : IComputerMoveOrchestrator
{
    public async Task<(MoveDto move, MoveResultDto result)> PlayAsync(GameState state)
    {
        var engine = (state.CurrentPlayer == PieceColor.White ? state.WhiteComputer : state.BlackComputer)
            ?? throw new InvalidOperationException($"No engine is set for {state.CurrentPlayer} in game {state.GameId}.");

        var uci = await engine.GetBestMoveAsync(state.ToFen(), state.RepetitionHistory());
        var move = uci.ToMoveDto(state, CurrentPlayerId(state));

        return await ApplyAndBroadcastAsync(state, move);
    }

    public async Task PlayRandomMoveAsync(GameState state)
    {
        var options = chessService.GetAllLegalMoves(state);

        // No legal moves means the game is already over; let the caller's loop detect it.
        if (options.Count == 0)
            return;

        var (piece, moves) = options[Random.Shared.Next(options.Count)];
        var target = moves[Random.Shared.Next(moves.Count)];

        var move = new MoveDto
        {
            GameId = state.GameId,
            PlayerId = CurrentPlayerId(state),
            PieceId = piece.Id,
            SourceRow = piece.Position.Row,
            SourceCol = piece.Position.Col,
            TargetRow = target.Row,
            TargetCol = target.Col,
            PromotionChoice = null   // a pawn cannot reach the last rank within the opening plies
        };

        await ApplyAndBroadcastAsync(state, move);
    }

    private async Task<(MoveDto move, MoveResultDto result)> ApplyAndBroadcastAsync(GameState state, MoveDto move)
    {
        var result = chessService.MakeMove(state, move);

        // Record the played position for training (no-op for non-training games).
        if (state.Trainer != nint.Zero)
            weightsStore.Record(state.Trainer, state.ToFen());

        await chessHub.Clients.Group(state.GameId.ToString())
            .SendAsync("ReceiveMoveUpdate", state.GameId.ToString(), move, result, state.ToDto());

        return (move, result);
    }

    private static Guid CurrentPlayerId(GameState state) =>
        state.CurrentPlayer == PieceColor.White ? state.WhitePlayerId : state.BlackPlayerId;
}
