using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Drives a computer move: asks the engine for a move, applies it through the rules
/// service, and broadcasts the result to the game's clients.
/// </summary>
public interface IComputerMoveOrchestrator
{
    /// <summary>
    /// Has the engine pick a move for the current position, applies it, and broadcasts it.
    /// </summary>
    /// <param name="state">The game to play a move in.</param>
    /// <param name="engine">The engine that selects the move.</param>
    /// <returns>The applied move and its result.</returns>
    Task<(MoveDto move, MoveResultDto result)> PlayAsync(GameState state, IChessEngine engine);
}
