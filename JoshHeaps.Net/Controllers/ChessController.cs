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

    /// <summary>
    /// Create a new chess game and store it in-memory.
    /// </summary>
    [HttpGet("new")]
    [HttpGet("new/{difficulty}")]
    public ActionResult CreateGame(int difficulty = 20)
    {
        var gameState = chessService.CreateNewGame();
        _games[gameState.GameId] = gameState;

        gameState.IsVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        Guid playerId = Guid.NewGuid();
        Guid computerId = Guid.NewGuid();
        var isWhite = Random.Shared.Next(2) == 0;

        gameState.Computer = engineFactory.Create(difficulty);

        if (isWhite)
        {
            gameState.WhitePlayerId = playerId;
            gameState.BlackPlayerId = computerId;
        }
        else
        {
            gameState.WhitePlayerId = computerId;
            gameState.BlackPlayerId = playerId;
            queue.Queue(async () =>
            {
                // Give user's browser time to connect to signalR and such.
                await Task.Delay(TimeSpan.FromSeconds(1));
                await orchestrator.PlayAsync(gameState, gameState.Computer!);
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
    /// Create a game the computer plays against itself and auto-play it move by move,
    /// broadcasting each move so it can be watched on the spectator page.
    /// </summary>
    [HttpGet("watch/cpu")]
    [HttpGet("watch/cpu/{difficulty}")]
    public ActionResult CreateSelfPlayGame(int difficulty = 4)
    {
        var gameState = chessService.CreateNewGame();
        _games[gameState.GameId] = gameState;

        gameState.IsVsComputer = true;
        gameState.IsComputerVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        gameState.WhitePlayerId = Guid.NewGuid();
        gameState.BlackPlayerId = Guid.NewGuid();
        gameState.Computer = engineFactory.Create(difficulty);

        ScheduleRemoveGame(gameState.GameId, _computerGameTimeout);
        StartSelfPlay(gameState);

        return Ok(new { gameState.GameId });
    }

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
                CurrentPlayer = g.CurrentPlayer.ToString(),
                MoveCount = g.MoveHistory.Count,
                g.IsCheck
            })
            .OrderByDescending(g => g.MoveCount);

        return Ok(activeGames);
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

        if (!isGameOver && gameState.IsVsComputer && gameState.Computer is not null)
            queue.Queue(() => orchestrator.PlayAsync(gameState, gameState.Computer!));

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
    /// Drives a computer-vs-computer game: keeps asking the engine for the side-to-move's
    /// move (which applies and broadcasts it) until the game ends or is removed.
    /// </summary>
    private void StartSelfPlay(GameState gameState)
    {
        queue.Queue(async () =>
        {
            // Give spectators a moment to join the SignalR group before the first move.
            await Task.Delay(TimeSpan.FromSeconds(1));

            while (_games.ContainsKey(gameState.GameId)
                && !gameState.IsCheckmate
                && !gameState.IsStalemate
                && !gameState.IsThreefoldRepetition
                && !gameState.IsForfeited)
            {
                try
                {
                    await orchestrator.PlayAsync(gameState, gameState.Computer!);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Self-play game {gameState.GameId} stopped: {ex.Message}");
                    break;
                }

                await Task.Delay(_selfPlayMoveDelay);
            }

            // Leave the finished game in place briefly so spectators can see the result.
            if (_games.ContainsKey(gameState.GameId))
                ScheduleRemoveGame(gameState.GameId, _selfPlayResultTimeout);
        });
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

                if (_games.TryGetValue(id, out var game) && game.Computer is not null)
                    await game.Computer.DisposeAsync();

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
