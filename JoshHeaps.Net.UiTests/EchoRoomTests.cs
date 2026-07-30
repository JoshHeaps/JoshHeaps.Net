using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace JoshHeaps.Net.UiTests;

/// <summary>
/// Drives two real browsers through a real room: the hub, the round scheduler, slot rotation,
/// detection and the solve all run unchanged. Only the microphone is synthetic, so the answer is
/// known in advance — this is everything except the acoustics.
/// </summary>
[TestFixture]
public class EchoRoomTests : PlaywrightTest
{
    private const double TargetMetres = 2.5;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly List<IBrowserContext> _contexts = [];
    private TestConfiguration Config => TestConfiguration.Instance;

    [OneTimeSetUp]
    public async Task LaunchBrowser()
    {
        // Its own Playwright instance and browser: the fake-media launch flags have to be set at
        // launch time, and the fixture-managed browser is already running by the time tests start.
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--use-fake-ui-for-media-stream",
                "--use-fake-device-for-media-stream",
                "--autoplay-policy=no-user-gesture-required"
            ]
        });
    }

    [OneTimeTearDown]
    public async Task CloseBrowser()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [TearDown]
    public async Task CloseContexts()
    {
        foreach (var context in _contexts) await context.CloseAsync();
        _contexts.Clear();
    }

    [Test]
    public async Task Two_Devices_Measure_The_Distance_Between_Them()
    {
        var roomCode = $"T{Random.Shared.Next(1000, 9999)}";
        var first = await NewDeviceAsync(roomCode, "laptop");
        var second = await NewDeviceAsync(roomCode, "phone");

        await Expect(first.Locator("#echoRoster li")).ToHaveCountAsync(2);

        var measured = await WaitForMeasurementAsync(first);
        var alsoMeasured = await WaitForMeasurementAsync(second);

        Assert.That(measured, Is.EqualTo(TargetMetres).Within(0.05), "the first device's range");
        Assert.That(alsoMeasured, Is.EqualTo(TargetMetres).Within(0.05), "both devices should agree");
    }

    [Test]
    public async Task A_Device_Leaving_Stops_The_Rounds_And_Rejoining_Resumes_Them()
    {
        var roomCode = $"T{Random.Shared.Next(1000, 9999)}";
        var first = await NewDeviceAsync(roomCode, "laptop");
        var second = await NewDeviceAsync(roomCode, "phone");

        await WaitForMeasurementAsync(first);
        await second.ClickAsync("#echoLeave");

        await Expect(first.Locator("#echoRoster li")).ToHaveCountAsync(1);
        await Expect(first.Locator("#echoStatus")).ToContainTextAsync("Waiting for a second device");

        await second.ClickAsync("#echoJoin");
        await Expect(first.Locator("#echoRoster li")).ToHaveCountAsync(2);
        Assert.That(await WaitForMeasurementAsync(first), Is.EqualTo(TargetMetres).Within(0.05));
    }

    /// <summary>
    /// A stalled main thread is the two-tabs-on-one-machine failure: only one tab is visible, so the
    /// other gets throttled and its round handling runs late. A late chirp attributed to the wrong
    /// slot yields a plausible-looking but completely wrong range, so the requirement is not "always
    /// measures" — it is "never reports a wrong answer". A round it cannot hit must be sat out.
    /// </summary>
    [Test]
    public async Task A_Stalled_Device_Sits_Rounds_Out_Instead_Of_Reporting_Nonsense()
    {
        var roomCode = $"T{Random.Shared.Next(1000, 9999)}";
        var first = await NewDeviceAsync(roomCode, "laptop");
        var second = await NewDeviceAsync(roomCode, "phone", stallMilliseconds: 900);

        await Expect(first.Locator("#echoRoster li")).ToHaveCountAsync(2);
        await first.EvaluateAsync("() => { window.__seen = []; }");
        await first.EvaluateAsync("""
            () => {
                const original = EchoPage.renderSolved.bind(EchoPage);
                EchoPage.renderSolved = update => {
                    const range = update.solved?.matrix?.[0]?.[1];
                    if (range != null) window.__seen.push(range);
                    return original(update);
                };
            }
            """);

        await first.WaitForTimeoutAsync(20000);
        var seen = await first.EvaluateAsync<double[]>("() => window.__seen");
        var satOut = await second.EvaluateAsync<int>("() => EchoSession.skippedRounds");

        TestContext.Out.WriteLine($"reported ranges: {string.Join(", ", seen.Select(r => r.ToString("0.000")))}, sat out: {satOut}");
        Assert.That(satOut, Is.GreaterThan(0),
            "the stall must actually have cost the device some slots, or this test proves nothing");
        Assert.That(seen, Is.Not.Empty, "a stalled peer should still let some rounds through");
        Assert.That(seen, Is.All.EqualTo(TargetMetres).Within(0.05),
            "every range that gets reported must be right — a stalled device must sit the round out, not chirp late");
    }

    [Test]
    public async Task The_Capture_Worklet_Keeps_A_Continuous_Readable_Stream()
    {
        var page = await NewDeviceAsync($"T{Random.Shared.Next(1000, 9999)}", "laptop", fakeMicrophone: false);

        await page.WaitForFunctionAsync(
            "() => EchoAudio.highestFrame > 48000",
            null,
            new PageWaitForFunctionOptions { Timeout = 15000, PollingInterval = 100 });

        var capture = await page.EvaluateAsync<double[]>("""
            () => [
                EchoAudio.context.sampleRate,
                EchoAudio.warnings.length,
                EchoAudio.read(EchoAudio.highestFrame - 24000, 24000)?.length ?? 0,
                EchoAudio.read(EchoAudio.highestFrame + 1000, 100) === null ? 1 : 0,
                Math.abs(EchoAudio.frameAt(EchoAudio.context.currentTime) - EchoAudio.highestFrame)
            ]
            """);

        Assert.That(capture[0], Is.EqualTo(48000), "the pipeline assumes it got the rate it asked for");
        Assert.That(capture[1], Is.EqualTo(0), "a clean fake device should raise no capture warnings");
        Assert.That(capture[2], Is.EqualTo(24000), "recent audio must be readable out of the ring");
        Assert.That(capture[3], Is.EqualTo(1), "reads past the captured end must fail rather than return silence");
        Assert.That(capture[4], Is.LessThan(48000),
            "the frame index and the context clock must stay in the same domain — a scheduled playback time is converted straight into a recording position");
    }

    /// <summary>
    /// A page joined to the room with its microphone replaced by a synthesizer. Every slot's chirp
    /// is placed where a room of this geometry would put it, including a different unknown output
    /// latency per slot so the cancellation is actually exercised.
    /// </summary>
    private async Task<IPage> NewDeviceAsync(
        string roomCode,
        string name,
        bool fakeMicrophone = true,
        int stallMilliseconds = 0)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            Permissions = ["microphone"]
        });

        _contexts.Add(context);
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Type == "error") TestContext.Out.WriteLine($"[{name} console] {message.Text}");
        };

        await page.GotoAsync($"{Config.Test.BaseUrl}/echo?room={roomCode}");
        await page.FillAsync("#echoName", name);
        if (fakeMicrophone) await page.EvaluateAsync(FakeMicrophoneScript, TargetMetres);
        if (stallMilliseconds > 0) await page.EvaluateAsync(StallScript, stallMilliseconds);
        await page.ClickAsync("#echoJoin");

        return page;
    }

    /// <summary>Blocks the main thread on every round announcement, the way a throttled tab does.</summary>
    private const string StallScript = """
        stallMs => {
            const original = EchoSession.handleRoundStarting.bind(EchoSession);
            EchoSession.handleRoundStarting = schedule => {
                const until = performance.now() + stallMs;
                while (performance.now() < until) { /* hold the thread */ }
                return original(schedule);
            };
        }
        """;

    private static async Task<double> WaitForMeasurementAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => EchoPage.lastSolved?.solved?.matrix?.[0]?.[1] != null",
            null,
            new PageWaitForFunctionOptions { Timeout = 30000, PollingInterval = 250 });

        return await page.EvaluateAsync<double>("() => EchoPage.lastSolved.solved.matrix[0][1]");
    }

    /// <summary>
    /// Synthesizes what the microphone would have heard for this round. The device's own chirp is
    /// placed at the frame it was actually scheduled for rather than at its nominal slot position,
    /// so any drift between "when the round said to play" and "when playback was really booked"
    /// reaches the detector instead of being papered over by the harness.
    /// </summary>
    private const string FakeMicrophoneScript = """
        targetMetres => {
            const speedOfSound = 343;
            const epsilonMetres = 0.08;
            const latencyBySlot = [1400, 5200, 2600, 7100, 900, 4300, 3300, 6000];

            EchoAudio.read = (startFrame, length) => {
                const round = EchoSession.pending ?? EchoSession.lastRound;
                if (!round) return null;

                const chirp = EchoSession.chirp;
                const toSamples = metres => Math.round((metres / speedOfSound) * round.sampleRate);
                const recording = new Float32Array(length);

                round.schedule.slotOrder.forEach((_, slot) => {
                    const own = slot === round.ownSlot;
                    const origin = own
                        ? round.scheduledFrame - round.windowStart
                        : round.leadInSamples + slot * round.slotSamples;
                    const at = origin + latencyBySlot[slot] + toSamples(own ? epsilonMetres : targetMetres);
                    const amplitude = own ? 1.0 : 0.25;

                    for (let i = 0; i < chirp.length && at + i < length; i++) recording[at + i] += chirp[i] * amplitude;
                });

                for (let i = 0; i < length; i++) recording[i] += (Math.random() * 2 - 1) * 0.01;
                return recording;
            };
        }
        """;
}
