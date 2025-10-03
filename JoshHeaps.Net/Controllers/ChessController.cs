using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChessController(
    IChessService chessService,
    IHubContext<ChessHub> chessHub,
    IBackgroundTaskQueue queue) : ControllerBase
{
    /// <summary>
    /// Store of ongoing games.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, GameState> _games = [];

    private static ConcurrentDictionary<Guid, Task> _gameRemovalTasks = [];

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

        gameState.Computer = new(difficulty);

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
                await gameState.Computer.MakeMove(gameState, chessHub, chessService);
            });
        }

        ScheduleRemoveGame(gameState.GameId, TimeSpan.FromHours(1));

        return Ok(new
        {
            Id = playerId,
            IsWhite = isWhite,
            gameState.GameId
        });
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

        ScheduleRemoveGame(gameState.GameId, TimeSpan.FromDays(1));

        return Ok(new
        {
            Id = playerId,
            IsWhite = isWhite,
            gameState.GameId
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

        var response = new
        {
            gameState.GameId,
            CurrentPlayer = gameState.CurrentPlayer.ToString(),
            gameState.IsCheck,
            gameState.IsCheckmate,
            gameState.IsStalemate,
            EnPassantTarget = gameState.EnPassantTarget?.ToString() ?? null,
            gameState.WhiteCanCastleKingside,
            gameState.WhiteCanCastleQueenside,
            gameState.BlackCanCastleKingside,
            gameState.BlackCanCastleQueenside,
            Pieces = gameState.Pieces
                .Where(p => p.Position.Row >= 0)
                .Select(p => new {
                    p.Id,
                    p.Type,
                    p.Color,
                    p.Position.Row,
                    p.Position.Col,
                    p.HasMoved
                }),
            gameState.MoveHistory
        };

        return Ok(response);
    }

    /// <summary>
    /// Make a move in the specified game. 
    /// The test passes a JSON body with a MoveDto.
    /// </summary>
    [HttpPost("move")]
    public ActionResult MakeMove([FromBody] MoveDto moveDto)
    {
        if (!_games.TryGetValue(moveDto.GameId, out var gameState))
            return NotFound("Game not found");

        // Check if player is authorized to move
        var isWhiteMove = gameState.CurrentPlayer == PieceColor.White;
        var expectedPlayerId = isWhiteMove ? gameState.WhitePlayerId : gameState.BlackPlayerId;

        if (moveDto.PlayerId != expectedPlayerId)
            return Forbid("You are not the current player.");

        // Make sure player owns the piece
        var piece = gameState.Pieces.FirstOrDefault(p => p.Id == moveDto.PieceId);

        if (piece is null)
            return NotFound("Chess piece Id does not exist");

        if ((isWhiteMove && piece.Color != PieceColor.White) || (!isWhiteMove && piece?.Color != PieceColor.Black))
            return Forbid("You cannot move this piece.");

        var result = chessService.MakeMove(gameState, moveDto);

        if (!result.Success)
            return BadRequest(result);

        if (result.IsCheckmate || result.IsStalemate)
        {
            // queue game removal
            ScheduleRemoveGame(gameState.GameId, TimeSpan.FromMinutes(1));
        }
        else
        {
            // increase timeout if play continues.
            if (gameState.IsVsComputer)
                ScheduleRemoveGame(gameState.GameId, TimeSpan.FromHours(1));
            else
                ScheduleRemoveGame(gameState.GameId, TimeSpan.FromDays(1));
        }

        if (gameState.IsVsComputer && gameState.Computer is not null)
            queue.Queue(() => gameState.Computer.MakeMove(gameState, chessHub, chessService));

        return Ok(result);
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

    private static void ScheduleRemoveGame(Guid id, TimeSpan delay)
    {
        if (_gameRemovalTasks.ContainsKey(id))
        {
            _gameRemovalTasks[id] = Task.Run(async () =>
            {
                await Task.Delay(delay);

                if (_games[id].Computer is not null)
                    await _games[id].Computer!.DisposeAsync();

                _games.Remove(id, out _);
                _gameRemovalTasks.Remove(id, out _);
            });

            return;
        }

        _gameRemovalTasks.TryAdd(id, Task.Run(async () =>
        {
            await Task.Delay(delay);

            if (_games[id].Computer is not null)
                await _games[id].Computer!.DisposeAsync();

            _games.Remove(id, out _);
            _gameRemovalTasks.Remove(id, out _);
        }));
    }
}
