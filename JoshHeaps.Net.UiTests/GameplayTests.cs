using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace JoshHeaps.Net.UiTests;

[TestFixture]
public class GameplayTests : PageTest
{
    private TestConfiguration Config => TestConfiguration.Instance;

    public override BrowserNewContextOptions ContextOptions()
    {
        return Config.GetBrowserContextOptions();
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
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

    [Test]
    public async Task Scholars_Mate_Results_In_Checkmate()
    {
        // Start a multiplayer game
        await Page.ClickAsync("#startGameBtn");
        await Page.WaitForTimeoutAsync(3000);
        await Page.WaitForSelectorAsync(".chessPiece");

        // Create a second browser context for the second player
        await using var browser = await Playwright.Chromium.LaunchAsync();
        await using var context2 = await browser.NewContextAsync();
        var page2 = await context2.NewPageAsync();
        await page2.GotoAsync($"{Config.Test.BaseUrl}/chess");
        await page2.ClickAsync("#startGameBtn");
        await page2.WaitForTimeoutAsync(3000);
        await page2.WaitForSelectorAsync(".chessPiece");

        /*
         * Scholar's Mate sequence:
         * 1. e4 e5
         * 2. Bc4 Nc6
         * 3. Qh5 Nf6??
         * 4. Qxf7# (checkmate)
         * 
         * In source square to destination square, accounting for black ui swapping:
         * 1. 52-36 51-35
         * 2. 61-34 58-37
         * 3. 59-45 62-45
         * 4. 45-13
         */

        // Move 1: White plays e4 (pawn e2-e4)
        await MakeMove(Page, 52, 36); // e2 to e4

        // Move 1: Black plays e5 (pawn e7-e5)
        await MakeMove(page2, 51, 35); // e7 to e5

        // Move 2: White plays Bc4 (bishop f1-c4)
        await MakeMove(Page, 61, 34); // f1 to c4

        // Move 2: Black plays Nc6 (knight b8-c6)
        await MakeMove(page2, 58, 37); // b8 to c6

        // Move 3: White plays Qh5 (queen d1-h5)
        await MakeMove(Page, 59, 45); // d1 to h5

        // Move 3: Black plays Nf6 (knight g8-f6) - the blunder
        await MakeMove(page2, 62, 45); // g8 to f6

        string? alertMessage = null;

        Page.Dialog += async (_, dialog) =>
        {
            alertMessage = dialog.Message;
            await dialog.AcceptAsync();
        };

        // Move 4: White plays Qxf7# (queen h5-f7, checkmate)
        await MakeMove(Page, 45, 13); // h5 to f7

        // The test passes if we can execute all moves without errors
        // Specific checkmate verification depends on how your UI handles game end
        Assert.That(alertMessage, Is.Not.Null);
        Assert.That(alertMessage, Contains.Substring("Checkmate"));
    }

    [Test]
    public async Task Pawn_Promotion_Shows_Modal()
    {
        // Start a multiplayer game
        await Page.ClickAsync("#startGameBtn");
        await Page.WaitForSelectorAsync(".chessPiece");

        // Create a second browser context for the second player (black)
        await using var browser = await Playwright.Chromium.LaunchAsync();
        await using var context2 = await browser.NewContextAsync();
        var page2 = await context2.NewPageAsync();
        await page2.GotoAsync($"{Config.Test.BaseUrl}/chess");
        await page2.ClickAsync("#startGameBtn");
        await page2.WaitForSelectorAsync(".chessPiece");

        // Exact sequence: 1. h4 g5  2. hxg5 h6  3. gxh6 a6  4. h7 a5  5. hxg8=Q
        // in square numbers: 55-39 49-33 39-30 48-40 30-23 55-47 23-15 47-39 15-6

        // Move 1: White h4 (h2-h4, square 55 to 39)
        await MakeMove(Page, 55, 39); // h2 to h4

        // Move 1: Black g5 (g7-g5, square 14 to 30)
        await MakeMove(page2, 49, 33); // g7 to g5

        // Move 2: White hxg5 (h4 captures g5, square 39 to 30)
        await MakeMove(Page, 39, 30); // h4 captures g5

        // Move 2: Black h6 (h7-h6, square 15 to 23)
        await MakeMove(page2, 48, 40); // h7 to h6

        // Move 3: White gxh6 (g5 captures h6, square 30 to 23)
        await MakeMove(Page, 30, 23); // g5 captures h6

        // Move 3: Black a6 (a7-a6, square 8 to 16)
        await MakeMove(page2, 55, 47); // a7 to a6

        // Move 4: White h7 (h6-h7, square 23 to 15)
        await MakeMove(Page, 23, 15); // h6 to h7

        // Move 4: Black a5 (a6-a5, square 16 to 24)
        await MakeMove(page2, 47, 39); // a6 to a5

        // Move 5: White hxg8=Q (h7 captures g8 and promotes, square 15 to 6)
        await MakeMove(Page, 15, 6); // h7 captures g8 (promotion)

        // Check if promotion modal appears
        var promotionModal = Page.Locator("#promotionModal");
        var queenOption = Page.Locator("button:has(img[alt='Queen'])");
        var promotedQueenSquare = Page.Locator("#square-6:has(img[src='/images/Chess Images/WhiteQueen.svg'])");
        var isModalVisible = await promotionModal.IsVisibleAsync();

        await Expect(promotionModal).ToBeVisibleAsync();
        await Expect(queenOption).ToBeVisibleAsync();

        await queenOption.ClickAsync();

        await promotionModal.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Expect(promotedQueenSquare).ToBeVisibleAsync();
    }

    private async Task MakeMove(IPage page, int fromSquare, int toSquare)
    {
        // Click on the source square/piece
        var fromSquareElement = page.Locator($"#square-{fromSquare}");
        var piece = fromSquareElement.Locator(".chessPiece");

        if (await piece.CountAsync() > 0)
        {
            await piece.ClickAsync();
            await page.WaitForTimeoutAsync(500);
        }

        // Click on the destination square
        var toSquareElement = page.Locator($"#square-{toSquare}");
        await toSquareElement.ClickAsync();
        await page.WaitForTimeoutAsync(1000);
    }
}