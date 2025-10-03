using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace JoshHeaps.Net.UiTests;

[TestFixture]
public class MultiplayerTests : PageTest
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
    public async Task Two_Players_Can_Join_Same_Game()
    {
        // This test requires running two browser contexts
        await using var browser = await Playwright.Chromium.LaunchAsync();
        await using var context1 = await browser.NewContextAsync(ContextOptions());
        await using var context2 = await browser.NewContextAsync(ContextOptions());

        var page1 = await context1.NewPageAsync();
        var page2 = await context2.NewPageAsync();

        await page1.GotoAsync($"{Config.Test.BaseUrl}/chess");
        await page2.GotoAsync($"{Config.Test.BaseUrl}/chess");

        // Player 1 starts a game
        await page1.ClickAsync("#startGameBtn");
        await page1.Locator(".chessPiece").First.WaitForAsync();

        // Player 2 joins the same game
        await page2.ClickAsync("#startGameBtn");
        await page2.Locator(".chessPiece").First.WaitForAsync();

        // Both players should see pieces
        var pieces1 = page1.Locator(".chessPiece");
        var pieces2 = page2.Locator(".chessPiece");

        await Expect(pieces1).ToHaveCountAsync(32);
        await Expect(pieces2).ToHaveCountAsync(32);
    }

    [Test]
    public async Task SignalR_Connection_Established()
    {
        // Start a game to trigger SignalR connection
        await Page.ClickAsync("#startGameBtn");
        await Page.Locator(".chessPiece").First.WaitForAsync();

        // Check browser console for SignalR connection message
        var consoleLogs = new List<string>();
        Page.Console += (_, e) => consoleLogs.Add(e.Text);

        await Page.ReloadAsync();
        await Page.WaitForTimeoutAsync(1000);

        // Should see SignalR connected message in console
        Assert.That(consoleLogs, Does.Contain("✅ SignalR connected").Or.Contain("SignalR connected"));
    }
}