using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;
using System.Text.Json;

namespace JoshHeaps.Net.UiTests;

[TestFixture]
public class ApiTests : PageTest
{
    private IAPIRequestContext? _apiContext;
    private TestConfiguration Config => TestConfiguration.Instance;

    [SetUp]
    public async Task Setup()
    {
        _apiContext = await Playwright.APIRequest.NewContextAsync(new()
        {
            BaseURL = Config.Test.BaseUrl,
            IgnoreHTTPSErrors = true
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_apiContext != null)
        {
            await _apiContext.DisposeAsync();
        }
    }

    [Test]
    public async Task JoinGame_Returns_Valid_Response()
    {
        var response = await _apiContext!.GetAsync("/api/chess/JoinGame");

        Assert.That(response.Status, Is.EqualTo(200));

        var jsonResponse = await response.JsonAsync();
        var gameData = JsonSerializer.Deserialize<JsonElement>(jsonResponse.ToString()!);

        Assert.That(gameData.TryGetProperty("id", out _), Is.True);
        Assert.That(gameData.TryGetProperty("isWhite", out _), Is.True);
        Assert.That(gameData.TryGetProperty("gameId", out _), Is.True);
    }

    [Test]
    public async Task CreateCPUGame_Returns_Valid_Response()
    {
        var response = await _apiContext!.GetAsync("/api/chess/new/5");

        Assert.That(response.Status, Is.EqualTo(200));

        var jsonResponse = await response.JsonAsync();
        var gameData = JsonSerializer.Deserialize<JsonElement>(jsonResponse.ToString()!);

        Assert.That(gameData.TryGetProperty("id", out _), Is.True);
        Assert.That(gameData.TryGetProperty("isWhite", out _), Is.True);
        Assert.That(gameData.TryGetProperty("gameId", out _), Is.True);
    }

    [Test]
    public async Task GetGameState_Returns_Valid_Game_Data()
    {
        // First create a game
        var createResponse = await _apiContext!.GetAsync("/api/chess/JoinGame");
        var createData = JsonSerializer.Deserialize<JsonElement>((await createResponse.JsonAsync()).ToString()!);
        var gameId = createData.GetProperty("gameId").GetString();

        // Then get the game state
        var response = await _apiContext.GetAsync($"/api/chess/{gameId}");

        Assert.That(response.Status, Is.EqualTo(200));

        var jsonResponse = await response.JsonAsync();
        var gameState = JsonSerializer.Deserialize<JsonElement>(jsonResponse.ToString()!);

        Assert.That(gameState.TryGetProperty("gameId", out _), Is.True);
        Assert.That(gameState.TryGetProperty("currentPlayer", out _), Is.True);
        Assert.That(gameState.TryGetProperty("pieces", out var pieces), Is.True);

        // Should have 32 pieces initially
        Assert.That(pieces.GetArrayLength(), Is.EqualTo(32));
    }

    [Test]
    public async Task GetLegalMoves_Returns_Valid_Moves()
    {
        // First create a game
        var createResponse = await _apiContext!.GetAsync("/api/chess/JoinGame");
        var createData = JsonSerializer.Deserialize<JsonElement>((await createResponse.JsonAsync()).ToString()!);
        var gameId = createData.GetProperty("gameId").GetString();

        // Get game state to find a piece
        var gameStateResponse = await _apiContext.GetAsync($"/api/chess/{gameId}");
        var gameState = JsonSerializer.Deserialize<JsonElement>((await gameStateResponse.JsonAsync()).ToString()!);
        var pieces = gameState.GetProperty("pieces");
        var firstPiece = pieces[0];
        var pieceId = firstPiece.GetProperty("id").GetString();

        // Get legal moves for the piece
        var response = await _apiContext.GetAsync($"/api/chess/{gameId}/legalMoves/{pieceId}");

        Assert.That(response.Status, Is.EqualTo(200));

        var moves = await response.JsonAsync();
        Assert.That(moves, Is.Not.Null);
    }

    [Test]
    public async Task Invalid_GameId_Returns_NotFound()
    {
        var invalidGameId = Guid.NewGuid().ToString();
        var response = await _apiContext!.GetAsync($"/api/chess/{invalidGameId}");

        Assert.That(response.Status, Is.EqualTo(404));
    }
}