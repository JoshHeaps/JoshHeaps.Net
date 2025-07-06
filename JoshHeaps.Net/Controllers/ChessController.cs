using JoshHeaps.Net.DAL;
using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;
using JoshHeaps.Net.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChessController(
    IChessService chessService,
    IHubContext<ChessHub> chessHub,
    BackgroundTaskQueue queue,
    ChessDbAccess dbAccess,
    StockfishManager stockfishManager) : ControllerBase
{
    /// <summary>
    /// Store of ongoing games.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, GameState> _games = [];

    private static ConcurrentDictionary<Guid, Task> _gameRemovalTasks = [];

    private static ConcurrentDictionary<Guid, DateTimeOffset> _lastUpdated = [];

    private static ConcurrentDictionary<Guid, GameState> _deliquents = [];

    private static readonly Guid SystemId = Guid.NewGuid();

    /// <summary>
    /// Create a new chess game and store it in-memory.
    /// </summary>
    [HttpGet("new")]
    [HttpGet("new/{difficulty}")]
    public async Task<ActionResult> CreateGame(int difficulty = 20)
    {
        await CheckForGameState();

        var gameState = chessService.CreateNewGame();
        _games[gameState.GameId] = gameState;

        gameState.IsVsComputer = true;
        gameState.WhiteJoined = true;
        gameState.BlackJoined = true;
        gameState.ComputerDifficulty = difficulty;
        Guid playerId = Guid.NewGuid();
        Guid computerId = Guid.NewGuid();
        var isWhite = false;

        if (isWhite)
        {
            gameState.WhitePlayerId = playerId;
            gameState.BlackPlayerId = computerId;
            gameState.ComputerColor = PieceColor.Black;
        }
        else
        {
            gameState.WhitePlayerId = computerId;
            gameState.BlackPlayerId = playerId;
            gameState.ComputerColor = PieceColor.White;

            queue.Queue(async() =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (!await stockfishManager.Run(gameState))
                    _deliquents[gameState.GameId] = gameState;
            }, gameState.GameId);
        }

        await dbAccess.SaveAsync(gameState);

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
    public async Task<ActionResult> JoinGame()
    {
        await CheckForGameState();

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

        ScheduleRemoveGame(gameState.GameId, TimeSpan.FromMinutes(10));
        await dbAccess.SaveAsync(gameState);

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
    public async Task<ActionResult> GetGameState(Guid gameId)
    {
        var gameState = await CheckForGameState(gameId);

        if (gameState is null)
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
    public async Task<ActionResult> MakeMove([FromBody] MoveDto moveDto)
    {
        var gameState = await CheckForGameState(moveDto.GameId);

        if (gameState is null)
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

        var result = await chessService.MakeMove(gameState, moveDto);

        if (!result.Success)
            return BadRequest(result);

        queue.Queue(async () =>
        {
            if (!await stockfishManager.Run(gameState))
                _deliquents[gameState.GameId] = gameState;
        }, gameState.GameId);
        

        if (result.IsCheckmate || result.IsStalemate)
        {
            // queue game removal
            ScheduleRemoveGame(gameState.GameId, TimeSpan.FromMinutes(10));
        }
        else
        {
            // increase timeout if play continues.
            if (gameState.IsVsComputer)
                ScheduleRemoveGame(gameState.GameId, TimeSpan.FromMinutes(10));
            else
                ScheduleRemoveGame(gameState.GameId, TimeSpan.FromMinutes(10));
        }

        return Ok(result);
    }

    /// <summary>
    /// Get the legal moves for a specific piece in a specific game.
    /// </summary>
    [HttpGet("{gameId}/legalMoves/{pieceId}")]
    public async Task<ActionResult> GetLegalMoves(Guid gameId, string pieceId)
    {
        var gameState = await CheckForGameState(gameId);

        if (gameState is null)
            return NotFound("Game not found");

        var moves = chessService.GetLegalMovesForPiece(gameState, pieceId);

        return Ok(moves);
    }

    /// <summary>
    /// (Optional) Get all legal moves for the current player.
    /// This was in your snippet, so we'll keep it.
    /// </summary>
    [HttpGet("{gameId}/legalMoves")]
    public async Task<ActionResult> GetAllLegalMoves(Guid gameId)
    {
        var gameState = await CheckForGameState(gameId);

        if (gameState is null)
            return NotFound("Game not found");

        var allMoves = chessService.GetAllLegalMoves(gameState)
            .Select(x => new
            {
                PieceId = x.piece.Id,
                Moves = x.moves
            });

        await stockfishManager.Run(gameState);

        return Ok(allMoves);
    }

    private void ScheduleRemoveGame(Guid id, TimeSpan delay)
    {
        if (_gameRemovalTasks.ContainsKey(id))
        {
            _lastUpdated[id] = DateTimeOffset.UtcNow;

            return;
        }

        _gameRemovalTasks.TryAdd(id, Task.Run(async () =>
        {
            _lastUpdated[id] = DateTimeOffset.UtcNow;

            while (_lastUpdated[id].Add(delay) >= DateTimeOffset.UtcNow)
                await Task.Delay(TimeSpan.FromMinutes(10));

            await GuidStore.RemoveAsync(id);
            _games.Remove(id, out _);
            _gameRemovalTasks.Remove(id, out _);
        }));
    }

    private async Task<GameState?> CheckForGameState(Guid gameId = default)
    {
        if (!_deliquents.IsEmpty)
            queue.Queue(() => stockfishManager.RunDeliquents(_deliquents), SystemId);

        if (_games.TryGetValue(gameId, out var existingGame))
            return existingGame;

        var gameState = await dbAccess.LoadAsync(gameId);

        if (gameState is not null)
        {
            _games.TryAdd(gameState.GameId, gameState);
            
            if (gameState.ComputerColor == gameState.CurrentPlayer)
                _deliquents.TryAdd(gameState.GameId, gameState);
        }

        queue.Queue(() => stockfishManager.RunDeliquents(_deliquents), SystemId);

        return gameState;
    }
}
