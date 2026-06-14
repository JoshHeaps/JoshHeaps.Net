using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Runs CPU-vs-CPU games: builds the game and engines, plays a randomized opening (for training
/// variety), drives the move loop to completion, then trains the learned engine from the result.
/// </summary>
public sealed class SelfPlayCoordinator(
    IChessService chessService,
    IChessEngineFactory engineFactory,
    IComputerMoveOrchestrator orchestrator,
    ILearnedWeightsStore weightsStore,
    IGameStore gameStore) : ISelfPlayCoordinator
{
    private static readonly TimeSpan _computerGameTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan _selfPlayMoveDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _selfPlayResultTimeout = TimeSpan.FromSeconds(30);

    // Abandon a game that goes this long without a move being played — i.e. an engine (usually
    // Stockfish) that crashed or froze. The game is killed with no result recorded; if it was an
    // auto-training game the trainer schedules a replacement once this one's task completes.
    private static readonly TimeSpan _idleTimeout = TimeSpan.FromSeconds(60);

    // Plies of random legal moves at the start of a training game, so self-play and
    // engine-vs-engine games explore different lines instead of replaying one game.
    private const int _openingRandomPlies = 4;

    public (Guid GameId, Task Completion) StartGame(SelfPlayConfig config, CancellationToken cancellationToken = default)
    {
        var whiteComputer = engineFactory.Create(config.WhiteSkill, config.WhiteKind);
        IChessEngine blackComputer;
        try
        {
            blackComputer = engineFactory.Create(config.BlackSkill, config.BlackKind);
        }
        catch
        {
            // Don't leak the first engine if the second fails to start (e.g. Stockfish process).
            whiteComputer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        var gameState = chessService.CreateNewGame();
        gameState.IsVsComputer = true;
        gameState.IsComputerVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        gameState.WhitePlayerId = Guid.NewGuid();
        gameState.BlackPlayerId = Guid.NewGuid();
        gameState.WhiteEngineKind = config.WhiteKind;
        gameState.BlackEngineKind = config.BlackKind;
        gameState.WhiteComputer = whiteComputer;
        gameState.BlackComputer = blackComputer;

        // When the learned engine is playing, attach a trainer so the outcome can train it.
        if (config.WhiteKind == ChessEngineKind.CustomLearned || config.BlackKind == ChessEngineKind.CustomLearned)
            gameState.Trainer = weightsStore.CreateTrainer();

        gameStore.Add(gameState);
        gameStore.ScheduleRemove(gameState.GameId, _computerGameTimeout);

        var completion = Task.Run(() => RunAsync(gameState, cancellationToken));
        return (gameState.GameId, completion);
    }

    /// <summary>
    /// Drives the game to completion, then trains from it. Never throws — a failure (or a frozen
    /// engine) just ends the game and the trainer is always freed, so callers can await or ignore.
    /// </summary>
    private async Task RunAsync(GameState gameState, CancellationToken cancellationToken)
    {
        bool aborted = false;

        try
        {
            // Give spectators a moment to join the SignalR group before the first move.
            await Task.Delay(_selfPlayMoveDelay, cancellationToken);

            // Training games open with random moves so they don't replay the same line.
            if (gameState.Trainer != nint.Zero)
                for (int i = 0; i < _openingRandomPlies && IsLive(gameState, cancellationToken); i++)
                {
                    await orchestrator.PlayRandomMoveAsync(gameState);
                    await Task.Delay(_selfPlayMoveDelay, cancellationToken);
                }

            var lastMoveCount = gameState.MoveHistory.Count;
            var lastProgress = DateTime.UtcNow;

            while (IsLive(gameState, cancellationToken))
            {
                await PlayMoveWithTimeoutAsync(gameState, cancellationToken);

                // Watchdog: abandon the game if it stops making moves (a crashed or frozen engine
                // can leave PlayAsync returning without progressing). Reset the clock on a real
                // move; otherwise bail once nothing has happened for the idle timeout.
                if (gameState.MoveHistory.Count != lastMoveCount)
                {
                    lastMoveCount = gameState.MoveHistory.Count;
                    lastProgress = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastProgress > _idleTimeout)
                    throw new TimeoutException("no move played within the idle timeout");

                await Task.Delay(_selfPlayMoveDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) { aborted = true; /* service shutting down */ }
        catch (TimeoutException)
        {
            aborted = true;
            Console.WriteLine($"Self-play game {gameState.GameId} abandoned: no move for over " +
                              $"{_idleTimeout.TotalSeconds:n0}s (likely a crashed or frozen engine).");
        }
        catch (Exception ex)
        {
            aborted = true;
            Console.WriteLine($"Self-play game {gameState.GameId} stopped: {ex.Message}");
        }

        // A clean finish trains the learned engine; an abandoned game (cancelled, crashed, or
        // idle past the timeout) records nothing and just frees its trainer.
        if (aborted)
            DiscardTraining(gameState);
        else
            ApplyLearning(gameState);

        // A clean finish lingers briefly so spectators see the result; an abandoned game is torn
        // down immediately so its engines (and any crashed/frozen Stockfish process) are released.
        if (gameStore.Contains(gameState.GameId))
            gameStore.ScheduleRemove(gameState.GameId, aborted ? TimeSpan.Zero : _selfPlayResultTimeout);
    }

    /// <summary>
    /// Plays one engine move, abandoning it if it exceeds <see cref="_idleTimeout"/> (throwing
    /// <see cref="TimeoutException"/>) so a single hung move can't block the loop forever. The
    /// abandoned move's eventual fault — it errors once the game's engines are disposed — is
    /// observed so it isn't an unobserved task exception.
    /// </summary>
    private async Task PlayMoveWithTimeoutAsync(GameState gameState, CancellationToken cancellationToken)
    {
        var play = orchestrator.PlayAsync(gameState);
        try
        {
            await play.WaitAsync(_idleTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _ = play.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
    }

    private bool IsLive(GameState gameState, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && gameStore.Contains(gameState.GameId)
        && !IsGameOver(gameState);

    private static bool IsGameOver(GameState gameState) =>
        gameState.IsCheckmate || gameState.IsStalemate || gameState.IsThreefoldRepetition || gameState.IsForfeited;

    /// <summary>
    /// Feeds a finished training game's result into the learned weights, then frees the trainer.
    /// A checkmate is a full-strength result; a material-imbalance draw is a half-strength win
    /// for the lower-material side (holding a draw while down material is a success; only drawing
    /// while up is a failure). A balanced draw, forfeit, or unfinished game teaches nothing.
    /// </summary>
    private void ApplyLearning(GameState gameState)
    {
        if (gameState.Trainer == nint.Zero)
            return;

        if (TryDetermineOutcome(gameState, out var winner, out var weight))
            weightsStore.ApplyResult(gameState.Trainer, winner, weight);

        weightsStore.DestroyTrainer(gameState.Trainer);
        gameState.Trainer = nint.Zero;
    }

    /// <summary>
    /// Frees an abandoned game's trainer without recording any result — a cancelled, crashed, or
    /// idle-timed-out game teaches the model nothing.
    /// </summary>
    private void DiscardTraining(GameState gameState)
    {
        if (gameState.Trainer == nint.Zero)
            return;

        weightsStore.DestroyTrainer(gameState.Trainer);
        gameState.Trainer = nint.Zero;
    }

    /// <summary>
    /// Determines the trainable outcome of a finished game: the winning color and the reward
    /// weight. Returns false when the game teaches nothing (balanced draw, forfeit, unfinished).
    /// </summary>
    private static bool TryDetermineOutcome(GameState gameState, out PieceColor winner, out double weight)
    {
        winner = PieceColor.White;
        weight = 1.0;

        if (gameState.IsCheckmate)
        {
            // The side to move is the mated one, so the winner is the other color.
            winner = gameState.CurrentPlayer == PieceColor.White ? PieceColor.Black : PieceColor.White;
            return true;
        }

        if (gameState.IsStalemate || gameState.IsThreefoldRepetition)
        {
            var (white, black) = MaterialCounts(gameState);

            if (white == black)
                return false;                 // a balanced draw carries no signal

            winner = white < black ? PieceColor.White : PieceColor.Black;
            weight = 0.5;
            return true;
        }

        return false;                         // forfeit / unfinished
    }

    /// <summary>Total non-king material per side (P=1, N=B=3, R=5, Q=9), for draw adjudication.</summary>
    private static (int white, int black) MaterialCounts(GameState gameState)
    {
        int white = 0, black = 0;

        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                var piece = gameState.Board[row, col];

                if (piece is null)
                    continue;

                int value = piece.Type switch
                {
                    PieceType.Pawn => 1,
                    PieceType.Knight => 3,
                    PieceType.Bishop => 3,
                    PieceType.Rook => 5,
                    PieceType.Queen => 9,
                    _ => 0
                };

                if (piece.Color == PieceColor.White)
                    white += value;
                else
                    black += value;
            }

        return (white, black);
    }
}
