using System.Text.Json;
using System.Text;

class ChessApiTest
{
    private static readonly HttpClient client = new HttpClient { BaseAddress = new Uri("https://localhost:7118/api/") };

    static async Task Main()
    {
        Console.WriteLine("Starting Chess API Test...");

        var gameId = await CreateGame();
        var (player1Id, isWhite1) = await JoinGame();
        var (player2Id, isWhite2) = await JoinGame();
        
        await GetGameState(gameId);

        var pawnId = "wPawn4";
        await GetLegalMoves(gameId, pawnId);

        await MakeMove(gameId, player1Id, pawnId, 6, 4, 4, 4); // e2 to e4

        await MakeMove(gameId, player2Id, "bPawn4", 1, 4, 2, 4); // e7 to e6

        await MakeMove(gameId, player1Id, "wKing", 7, 4, 7, 6); // Try castling

        await MakeMove(gameId, player1Id, "wPawn4", 4, 4, 3, 4); // Move bishop

        await MakeMove(gameId, player2Id, "bPawn3", 1, 3, 3, 3); // d7 to d5

        await MakeMove(gameId, player1Id, "wPawn4", 4, 4, 2, 3); // En passant capture

        await MakeMove(gameId, player1Id, "wPawn7", 6, 7, 7, 7, PieceType.Queen); // Pawn Promotion

        await GetGameState(gameId);

        Console.WriteLine("Test completed.");
    }

    private static async Task<Guid> CreateGame()
    {
        var response = await client.PostAsync("Chess/new", null);
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());

        Guid gameId = Guid.Parse(data["gameId"].ToString());
        Console.WriteLine($"Game created: {gameId}");
        return gameId;
    }

    private static async Task<(Guid, bool)> JoinGame()
    {
        var response = await client.GetAsync("Chess/JoinGame");
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());

        Guid playerId = Guid.Parse(data["id"].ToString());
        bool isWhite = bool.Parse(data["isWhite"].ToString());

        Console.WriteLine($"Player joined: {playerId} (IsWhite: {isWhite})");
        return (playerId, isWhite);
    }

    private static async Task GetGameState(Guid gameId)
    {
        var response = await client.GetAsync($"Chess/{gameId}");
        Console.WriteLine($"Game state: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task GetLegalMoves(Guid gameId, string pieceId)
    {
        var response = await client.GetAsync($"Chess/{gameId}/legalMoves/{pieceId}");
        Console.WriteLine($"Legal moves for {pieceId}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task MakeMove(Guid gameId, Guid playerId, string pieceId, int sourceRow, int sourceCol, int targetRow, int targetCol, PieceType? promotion = null)
    {
        var payload = new
        {
            GameId = gameId,
            PlayerId = playerId,
            PieceId = pieceId,
            SourceRow = sourceRow,
            SourceCol = sourceCol,
            TargetRow = targetRow,
            TargetCol = targetCol,
            PromotionChoice = promotion
        };

        var jsonPayload = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("Chess/move", jsonPayload);
        Console.WriteLine($"Move {pieceId} to ({targetRow}, {targetCol}): {await response.Content.ReadAsStringAsync()}");
    }
}

public enum PieceType
{
    Pawn,
    Rook,
    Knight,
    Bishop,
    Queen,
    King
}