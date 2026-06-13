using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChessController(
    IChessService chessService,
    IBackgroundTaskQueue queue,
    IChessEngineFactory engineFactory,
    IComputerMoveOrchestrator orchestrator,
    ILearnedWeightsStore weightsStore,
    IHubContext<ChessHub> chessHub) : ControllerBase
{
    /// <summary>
    /// Store of ongoing games.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, GameState> _games = [];
    private static readonly ConcurrentDictionary<Guid, Task> _gameRemovalTasks = [];
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _gameRemovalCancellationTokens = [];

    private static readonly TimeSpan _computerGameTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan _multiplayerGameTimeout = TimeSpan.FromDays(1);
    private static readonly TimeSpan _gameCleanupTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _selfPlayMoveDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _selfPlayResultTimeout = TimeSpan.FromSeconds(30);

    // Plies of random legal moves at the start of a training game, so self-play and
    // engine-vs-engine games explore different lines instead of replaying one game.
    private const int _openingRandomPlies = 4;

    /// <summary>
    /// Create a new chess game and store it in-memory.
    /// </summary>
    [HttpGet("new")]
    [HttpGet("new/{difficulty}")]
    public ActionResult CreateGame(int difficulty = 20, string color = "random")
    {
        var gameState = chessService.CreateNewGame();
        _games[gameState.GameId] = gameState;

        gameState.IsVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        Guid playerId = Guid.NewGuid();
        Guid computerId = Guid.NewGuid();
        var isWhite = color.ToLowerInvariant() switch
        {
            "white" => true,
            "black" => false,
            _ => Random.Shared.Next(2) == 0,
        };

        var computer = engineFactory.Create(difficulty);

        if (isWhite)
        {
            gameState.WhitePlayerId = playerId;
            gameState.BlackPlayerId = computerId;
            gameState.BlackComputer = computer;
        }
        else
        {
            gameState.WhitePlayerId = computerId;
            gameState.BlackPlayerId = playerId;
            gameState.WhiteComputer = computer;
            queue.Queue(async () =>
            {
                // Give user's browser time to connect to signalR and such.
                await Task.Delay(TimeSpan.FromSeconds(1));
                await orchestrator.PlayAsync(gameState);
            });
        }

        ScheduleRemoveGame(gameState.GameId, _computerGameTimeout);

        return Ok(new
        {
            Id = playerId,
            IsWhite = isWhite,
            gameState.GameId
        });
    }

    /// <summary>
    /// Create a computer-vs-computer game and auto-play it move by move, broadcasting each
    /// move so it can be watched on the spectator page. Each side's engine and skill can be
    /// chosen independently; when the learned engine plays, the game also trains it.
    /// </summary>
    [HttpGet("watch/cpu")]
    [HttpGet("watch/cpu/{difficulty}")]
    public ActionResult CreateSelfPlayGame(
        int difficulty = 4,
        string whiteEngine = "custom",
        string blackEngine = "custom",
        int? whiteSkill = null,
        int? blackSkill = null)
    {
        var whiteKind = ParseEngineKind(whiteEngine);
        var blackKind = ParseEngineKind(blackEngine);

        var gameState = chessService.CreateNewGame();
        _games[gameState.GameId] = gameState;

        gameState.IsVsComputer = true;
        gameState.IsComputerVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        gameState.WhitePlayerId = Guid.NewGuid();
        gameState.BlackPlayerId = Guid.NewGuid();
        gameState.WhiteEngineKind = whiteKind;
        gameState.BlackEngineKind = blackKind;
        gameState.WhiteComputer = engineFactory.Create(whiteSkill ?? difficulty, whiteKind);
        gameState.BlackComputer = engineFactory.Create(blackSkill ?? difficulty, blackKind);

        // When the learned engine is playing, attach a trainer so the outcome can train it.
        if (whiteKind == ChessEngineKind.CustomLearned || blackKind == ChessEngineKind.CustomLearned)
            gameState.Trainer = weightsStore.CreateTrainer();

        ScheduleRemoveGame(gameState.GameId, _computerGameTimeout);
        StartSelfPlay(gameState);

        return Ok(new { gameState.GameId });
    }

    private static ChessEngineKind ParseEngineKind(string value) => value.ToLowerInvariant() switch
    {
        "stockfish" => ChessEngineKind.Stockfish,
        "customlearned" or "learned" => ChessEngineKind.CustomLearned,
        _ => ChessEngineKind.Custom
    };

    /// <summary>
    /// Joins the "pool" of chess players.
    /// Test code expects to receive a GUID for the player
    /// and a bool indicating if they are White.
    /// </summary>
    [HttpGet("JoinGame")]
    public ActionResult JoinGame()
    {
        Console.WriteLine("joining game");
        GameState? gameState = _games.Values.FirstOrDefault(g => g.IsOpen);

        if (gameState == null)
        {
            gameState = chessService.CreateNewGame();
            _games[gameState.GameId] = gameState;
        }

        Guid playerId = Guid.NewGuid();
        bool isWhite = true;

        if (!gameState.WhiteJoined)
        {
            gameState.WhiteJoined = true;
            gameState.WhitePlayerId = playerId;
        }
        else if (!gameState.BlackJoined)
        {
            gameState.BlackJoined = true;
            gameState.BlackPlayerId = playerId;
            isWhite = false;
        }

        ScheduleRemoveGame(gameState.GameId, _multiplayerGameTimeout);

        return Ok(new
        {
            Id = playerId,
            IsWhite = isWhite,
            gameState.GameId
        });
    }

    /// <summary>
    /// List in-progress games for spectators: both sides present and the game not yet decided.
    /// </summary>
    [HttpGet("active")]
    public ActionResult GetActiveGames()
    {
        var activeGames = _games.Values
            // In-progress games, plus finished computer-vs-computer games still in their result window.
            .Where(g => g.WhiteJoined && g.BlackJoined
                && ((!g.IsCheckmate && !g.IsStalemate && !g.IsForfeited && !g.IsThreefoldRepetition) || g.IsComputerVsComputer))
            .Select(g => new
            {
                g.GameId,
                g.IsVsComputer,
                g.IsComputerVsComputer,
                WhiteEngine = g.WhiteEngineKind.ToString(),
                BlackEngine = g.BlackEngineKind.ToString(),
                CurrentPlayer = g.CurrentPlayer.ToString(),
                MoveCount = g.MoveHistory.Count,
                g.IsCheck
            })
            .OrderByDescending(g => g.MoveCount);

        return Ok(activeGames);
    }

    /// <summary>
    /// The learned engine's piece-square bonus table, for the weights-visualization page:
    /// one 64-entry array per piece type (Pawn..King), white-relative (A1=0 .. H8=63).
    /// </summary>
    [HttpGet("weights")]
    public ActionResult GetLearnedWeights()
    {
        var names = new[] { "Pawn", "Knight", "Bishop", "Rook", "Queen", "King" };
        var featureNames = new[] { "Mobility N", "Mobility B", "Mobility R", "Mobility Q", "Passed", "Isolated", "Doubled", "King safety" };

        var snapshot = weightsStore.Snapshot();

        return Ok(new
        {
            mg = snapshot.Mg.Select((squares, i) => new { name = names[i], squares }),
            eg = snapshot.Eg.Select((squares, i) => new { name = names[i], squares }),
            features = snapshot.Features.Select((value, i) => new { name = featureNames[i], value })
        });
    }

    /// <summary>
    /// Get the state of an existing game by ID.
    /// </summary>
    [HttpGet("{gameId}")]
    public ActionResult GetGameState(Guid gameId)
    {
        if (!_games.TryGetValue(gameId, out var gameState))
            return NotFound("Game not found");

        return Ok(gameState.ToDto());
    }

    /// <summary>
    /// Make a move in the specified game. 
    /// The test passes a JSON body with a MoveDto.
    /// </summary>
    [HttpPost("move")]
    public async Task<ActionResult> MakeMove([FromBody] MoveDto moveDto)
    {
        if (!_games.TryGetValue(moveDto.GameId, out var gameState))
            return NotFound("Game not found");

        // Check if player is authorized to move
        var isWhiteMove = gameState.CurrentPlayer == PieceColor.White;
        var expectedPlayerId = isWhiteMove ? gameState.WhitePlayerId : gameState.BlackPlayerId;

        if (moveDto.PlayerId != expectedPlayerId)
            return StatusCode(403, "You are not the current player.");

        // Make sure player owns the piece
        var piece = gameState.Pieces.FirstOrDefault(p => p.Id == moveDto.PieceId);

        if (piece is null)
            return NotFound("Chess piece Id does not exist");

        if ((isWhiteMove && piece.Color != PieceColor.White) || (!isWhiteMove && piece?.Color != PieceColor.Black))
            return StatusCode(403, "You cannot move this piece.");

        var result = chessService.MakeMove(gameState, moveDto);

        if (!result.Success)
            return BadRequest(result);

        var isGameOver = result.IsCheckmate || result.IsStalemate || result.IsThreefoldRepetition;

        if (isGameOver)
            ScheduleRemoveGame(gameState.GameId, _gameCleanupTimeout);
        else if (gameState.IsVsComputer)
            ScheduleRemoveGame(gameState.GameId, _computerGameTimeout);
        else
            ScheduleRemoveGame(gameState.GameId, _multiplayerGameTimeout);

        var state = gameState.ToDto();

        // Broadcast the move (with the full resulting state) to everyone watching this
        // game. The mover also receives this echo but drops it via the version guard,
        // since it already rendered the same state from this response.
        await chessHub.Clients.Group(gameState.GameId.ToString())
            .SendAsync("ReceiveMoveUpdate", gameState.GameId.ToString(), moveDto, result, state);

        var sideToMoveEngine = gameState.CurrentPlayer == PieceColor.White
            ? gameState.WhiteComputer
            : gameState.BlackComputer;

        if (!isGameOver && gameState.IsVsComputer && sideToMoveEngine is not null)
            queue.Queue(() => orchestrator.PlayAsync(gameState));

        return Ok(new { result, state });
    }

    /// <summary>
    /// Forfeit a game on behalf of the calling player, handing the win to the opponent.
    /// Used when a player abandons a game (e.g. starts a new one mid-game).
    /// </summary>
    [HttpPost("forfeit")]
    public async Task<ActionResult> Forfeit([FromBody] ForfeitDto forfeit)
    {
        if (!_games.TryGetValue(forfeit.GameId, out var gameState))
            return NotFound("Game not found");

        if (gameState.IsCheckmate || gameState.IsStalemate || gameState.IsForfeited)
            return Ok();

        var isWhitePlayer = gameState.WhitePlayerId == forfeit.PlayerId;
        var isBlackPlayer = gameState.BlackPlayerId == forfeit.PlayerId;

        if (!isWhitePlayer && !isBlackPlayer)
            return StatusCode(403, "You are not a player in this game.");

        gameState.IsForfeited = true;
        gameState.Winner = isWhitePlayer ? PieceColor.Black : PieceColor.White;

        await chessHub.Clients.Group(gameState.GameId.ToString())
            .SendAsync("ReceiveGameOver", gameState.GameId.ToString(), gameState.Winner.ToString(), "forfeit");

        ScheduleRemoveGame(gameState.GameId, _gameCleanupTimeout);

        return Ok();
    }

    /// <summary>
    /// Export a game as PGN (works while the game is still in memory after it ends).
    /// </summary>
    [HttpGet("{gameId}/pgn")]
    public ActionResult GetPgn(Guid gameId)
    {
        if (!_games.TryGetValue(gameId, out var gameState))
            return NotFound("Game not found");

        return Content(gameState.ToPgn(), "application/x-chess-pgn");
    }

    /// <summary>
    /// Get the legal moves for a specific piece in a specific game.
    /// </summary>
    [HttpGet("{gameId}/legalMoves/{pieceId}")]
    public ActionResult GetLegalMoves(Guid gameId, string pieceId)
    {
        if (!_games.TryGetValue(gameId, out var gameState))
            return NotFound("Game not found");

        var moves = chessService.GetLegalMovesForPiece(gameState, pieceId);
        return Ok(moves);
    }

    /// <summary>
    /// (Optional) Get all legal moves for the current player.
    /// This was in your snippet, so we'll keep it.
    /// </summary>
    [HttpGet("{gameId}/legalMoves")]
    public ActionResult GetAllLegalMoves(Guid gameId)
    {
        if (!_games.TryGetValue(gameId, out var gameState))
            return NotFound("Game not found");

        var allMoves = chessService.GetAllLegalMoves(gameState)
            .Select(x => new
            {
                PieceId = x.piece.Id,
                Moves = x.moves
            });

        return Ok(allMoves);
    }

    /// <summary>
    /// Drives a computer-vs-computer game: keeps asking the side-to-move's engine for its
    /// move (which applies and broadcasts it) until the game ends or is removed. Training
    /// games get a randomized opening and feed their result back into the learned weights.
    /// </summary>
    private void StartSelfPlay(GameState gameState)
    {
        queue.Queue(async () =>
        {
            // Give spectators a moment to join the SignalR group before the first move.
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Training games open with random moves so they don't replay the same line.
            if (gameState.Trainer != nint.Zero)
                for (int i = 0; i < _openingRandomPlies && _games.ContainsKey(gameState.GameId) && !IsGameOver(gameState); i++)
                {
                    await orchestrator.PlayRandomMoveAsync(gameState);
                    await Task.Delay(_selfPlayMoveDelay);
                }

            while (_games.ContainsKey(gameState.GameId) && !IsGameOver(gameState))
            {
                try
                {
                    await orchestrator.PlayAsync(gameState);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Self-play game {gameState.GameId} stopped: {ex.Message}");
                    break;
                }

                await Task.Delay(_selfPlayMoveDelay);
            }

            ApplyLearning(gameState);

            // Leave the finished game in place briefly so spectators can see the result.
            if (_games.ContainsKey(gameState.GameId))
                ScheduleRemoveGame(gameState.GameId, _selfPlayResultTimeout);
        });
    }

    private static bool IsGameOver(GameState gameState) =>
        gameState.IsCheckmate || gameState.IsStalemate || gameState.IsThreefoldRepetition || gameState.IsForfeited;

    /// <summary>
    /// Feeds a finished training game's result into the learned weights, then frees the
    /// trainer. Both sides teach the table — the winner's squares/features up, the loser's
    /// down. A checkmate is a full-strength result; a material-imbalance draw is a half-
    /// strength win for the lower-material side (holding a draw while down material is a
    /// success; only drawing while up is a failure). A balanced draw, forfeit, or unfinished
    /// game teaches nothing (but the trainer is still freed).
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

    private static void ScheduleRemoveGame(Guid id, TimeSpan delay)
    {
        if (_gameRemovalCancellationTokens.TryRemove(id, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _gameRemovalCancellationTokens[id] = cts;

        _gameRemovalTasks[id] = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);

                if (_games.TryGetValue(id, out var game))
                {
                    if (game.WhiteComputer is not null)
                        await game.WhiteComputer.DisposeAsync();
                    if (game.BlackComputer is not null)
                        await game.BlackComputer.DisposeAsync();

                    // Free the trainer if the game never reached ApplyLearning (e.g. timed out).
                    // The native ABI is shared via CustomChessEngine's import resolver.
                    if (game.Trainer != nint.Zero)
                    {
                        CustomChessEngine.NativeMethods.trainer_destroy(game.Trainer);
                        game.Trainer = nint.Zero;
                    }
                }

                _games.Remove(id, out _);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_gameRemovalCancellationTokens.TryGetValue(id, out var currentCts) && currentCts == cts)
                {
                    _gameRemovalCancellationTokens.TryRemove(id, out _);
                }

                cts.Dispose();
            }
        });
    }
}
