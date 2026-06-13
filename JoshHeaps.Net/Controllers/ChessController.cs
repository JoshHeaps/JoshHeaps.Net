using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChessController(
    IChessService chessService,
    IBackgroundTaskQueue queue,
    IChessEngineFactory engineFactory,
    IComputerMoveOrchestrator orchestrator,
    ILearnedWeightsStore weightsStore,
    IGameStore gameStore,
    ISelfPlayCoordinator selfPlay,
    IHubContext<ChessHub> chessHub) : ControllerBase
{
    private static readonly TimeSpan _computerGameTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan _multiplayerGameTimeout = TimeSpan.FromDays(1);
    private static readonly TimeSpan _gameCleanupTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Create a new chess game and store it in-memory.
    /// </summary>
    [HttpGet("new")]
    [HttpGet("new/{difficulty}")]
    public ActionResult CreateGame(int difficulty = 20, string color = "random")
    {
        var gameState = chessService.CreateNewGame();
        gameStore.Add(gameState);

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

        gameStore.ScheduleRemove(gameState.GameId, _computerGameTimeout);

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
        var config = new SelfPlayConfig(
            ParseEngineKind(whiteEngine), whiteSkill ?? difficulty,
            ParseEngineKind(blackEngine), blackSkill ?? difficulty);

        var (gameId, _) = selfPlay.StartGame(config);

        return Ok(new { GameId = gameId });
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
        GameState? gameState = gameStore.All.FirstOrDefault(g => g.IsOpen);

        if (gameState == null)
        {
            gameState = chessService.CreateNewGame();
            gameStore.Add(gameState);
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

        gameStore.ScheduleRemove(gameState.GameId, _multiplayerGameTimeout);

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
        var activeGames = gameStore.All
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
        var featureNames = new[] { "Mobility N", "Mobility B", "Mobility R", "Mobility Q", "Passed", "Pawn links", "King safety" };

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
        if (!gameStore.TryGet(gameId, out var gameState))
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
        if (!gameStore.TryGet(moveDto.GameId, out var gameState))
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
            gameStore.ScheduleRemove(gameState.GameId, _gameCleanupTimeout);
        else if (gameState.IsVsComputer)
            gameStore.ScheduleRemove(gameState.GameId, _computerGameTimeout);
        else
            gameStore.ScheduleRemove(gameState.GameId, _multiplayerGameTimeout);

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
        if (!gameStore.TryGet(forfeit.GameId, out var gameState))
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

        gameStore.ScheduleRemove(gameState.GameId, _gameCleanupTimeout);

        return Ok();
    }

    /// <summary>
    /// Export a game as PGN (works while the game is still in memory after it ends).
    /// </summary>
    [HttpGet("{gameId}/pgn")]
    public ActionResult GetPgn(Guid gameId)
    {
        if (!gameStore.TryGet(gameId, out var gameState))
            return NotFound("Game not found");

        return Content(gameState.ToPgn(), "application/x-chess-pgn");
    }

    /// <summary>
    /// Get the legal moves for a specific piece in a specific game.
    /// </summary>
    [HttpGet("{gameId}/legalMoves/{pieceId}")]
    public ActionResult GetLegalMoves(Guid gameId, string pieceId)
    {
        if (!gameStore.TryGet(gameId, out var gameState))
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
        if (!gameStore.TryGet(gameId, out var gameState))
            return NotFound("Game not found");

        var allMoves = chessService.GetAllLegalMoves(gameState)
            .Select(x => new
            {
                PieceId = x.piece.Id,
                Moves = x.moves
            });

        return Ok(allMoves);
    }

}
