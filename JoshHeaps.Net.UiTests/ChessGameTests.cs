using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace JoshHeaps.Net.UiTests;

[TestFixture]
public class ChessGameTests : PageTest
{
    private TestConfiguration Config => TestConfiguration.Instance;

    public override BrowserNewContextOptions ContextOptions()
    {
        return new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = Config.Playwright.Viewport.Width,
                Height = Config.Playwright.Viewport.Height
            }
        };
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Set Playwright browser options for headless control
        if (!Config.Playwright.Headless)
        {
            Environment.SetEnvironmentVariable("HEADED", "1");
        }
        if (Config.Playwright.SlowMotion > 0)
        {
            Environment.SetEnvironmentVariable("PWSLOWMO", Config.Playwright.SlowMotion.ToString());
        }
    }

    [SetUp]
    public async Task Setup()
    {
        await Page.GotoAsync($"{Config.Test.BaseUrl}/chess");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    protected async Task WaitDefault()
    {
        await Page.WaitForTimeoutAsync(Config.Test.WaitTimeout);
    }

    [Test]
    public async Task Chess_Page_Loads_Successfully()
    {
        await Expect(Page.Locator("h1")).ToContainTextAsync("Chess");
        await Expect(Page.Locator("#startGameBtn")).ToBeVisibleAsync();
        await Expect(Page.Locator("#startCPUGame")).ToBeVisibleAsync();
        await Expect(Page.Locator("#chessBoard")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Chess_Board_Has_64_Squares()
    {
        var squares = Page.Locator(".chessSquare");
        await Expect(squares).ToHaveCountAsync(64);
    }

    [Test]
    public async Task Start_New_Game_Button_Works()
    {
        await Page.ClickAsync("#startGameBtn");

        // Wait for game to start (pieces should appear)
        await Page.Locator(".chessPiece").First.WaitForAsync();

        // Check that pieces have been rendered
        var pieces = Page.Locator(".chessPiece");
        await Expect(pieces).ToHaveCountAsync(32); // Standard chess has 32 pieces
    }

    [Test]
    public async Task Start_CPU_Game_Shows_Difficulty_Modal()
    {
        await Page.ClickAsync("#startCPUGame");

        // Should show difficulty modal
        await Expect(Page.Locator("#difficultyModal")).ToBeVisibleAsync();
        await Expect(Page.Locator("#difficultyModal p")).ToContainTextAsync("Set bot difficulty to:");

        // Should have difficulty buttons 1-20
        var difficultyButtons = Page.Locator("#difficultyButtonContainer button");
        await Expect(difficultyButtons).ToHaveCountAsync(20);
    }

    [Test]
    public async Task CPU_Game_Starts_After_Selecting_Difficulty()
    {
        await Page.ClickAsync("#startCPUGame");

        // Select difficulty 5
        await Page.ClickAsync("#difficultyButtonContainer button:nth-child(5)");

        // Modal should close
        await Expect(Page.Locator("#difficultyModal")).ToBeHiddenAsync();

        // Wait for game to start
        await Page.Locator(".chessPiece").First.WaitForAsync();

        // Check that pieces have been rendered
        var pieces = Page.Locator(".chessPiece");
        await Expect(pieces).ToHaveCountAsync(32);
    }

    [Test]
    public async Task Pieces_WhenClicked_HighlightLegalMoves()
    {
        // Start a new game first
        await Page.ClickAsync("#startGameBtn");

        // Wait for pieces to be rendered
        await Page.Locator(".chessPiece").First.WaitForAsync();

        // Find any white piece and click it
        var pawn = Page.Locator("#chessBoard div:has(img[src*='WhitePawn'])").First;
        await pawn.ClickAsync();

        var legalMoves = Page.Locator(".legal");
        await Expect(legalMoves).ToHaveCountAsync(2);
    }
}