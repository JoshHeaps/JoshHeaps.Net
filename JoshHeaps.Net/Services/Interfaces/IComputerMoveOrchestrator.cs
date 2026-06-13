using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Drives a computer move: asks the side-to-move's engine for a move, applies it through
/// the rules service, and broadcasts the result to the game's clients.
/// </summary>
public interface IComputerMoveOrchestrator
{
    /// <summary>
    /// Has the side-to-move's engine pick a move for the current position, applies it, and
    /// broadcasts it. The engine is taken from the game's per-side computer assignments.
    /// </summary>
    /// <param name="state">The game to play a move in.</param>
    /// <returns>The applied move and its result.</returns>
    Task<(MoveDto move, MoveResultDto result)> PlayAsync(GameState state);

    /// <summary>
    /// Plays a uniformly-random legal move for the side to move (no engine), applying and
    /// broadcasting it. Used to randomize the opening of training games so self-play and
    /// engine-vs-engine games don't replay the same line every time.
    /// </summary>
    /// <param name="state">The game to play a random move in.</param>
    Task PlayRandomMoveAsync(GameState state);
}
