using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace JoshHeaps.Net.UiTests;

public class TestConfiguration
{
    private static TestConfiguration? _instance;
    private static readonly object _lock = new object();

    public PlaywrightSettings Playwright { get; }
    public TestSettings Test { get; }

    private TestConfiguration()
    {
        var isCI = Environment.GetEnvironmentVariable("CI") == "true" ||
                   Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

        var configFile = isCI ? "appsettings.CI.json" : "appsettings.Integration.json";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile(configFile, optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Integration.json", optional: true, reloadOnChange: false) // fallback
            .Build();

        Playwright = configuration.GetSection("PlaywrightSettings").Get<PlaywrightSettings>() ?? new PlaywrightSettings();
        Test = configuration.GetSection("TestSettings").Get<TestSettings>() ?? new TestSettings();
    }

    public static TestConfiguration Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new TestConfiguration();
                    }
                }
            }
            return _instance;
        }
    }

    public BrowserNewContextOptions GetBrowserContextOptions()
    {
        return new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = Playwright.Viewport.Width,
                Height = Playwright.Viewport.Height
            },
            RecordVideoDir = Playwright.EnableVideoRecording ? Path.Combine(Directory.GetCurrentDirectory(), "test-results", "videos") : null,
            IgnoreHTTPSErrors = Playwright.IgnoreHTTPSErrors
        };
    }

    public BrowserTypeLaunchOptions GetLaunchOptions()
    {
        return new BrowserTypeLaunchOptions
        {
            Headless = Playwright.Headless,
            SlowMo = Playwright.SlowMotion,
            Timeout = Playwright.BrowserTimeout
        };
    }
}

public class PlaywrightSettings
{
    public bool Headless { get; set; } = true;
    public float SlowMotion { get; set; } = 0;
    public ViewportSettings Viewport { get; set; } = new();
    public float BrowserTimeout { get; set; } = 30000;
    public float ActionTimeout { get; set; } = 10000;
    public bool EnableVideoRecording { get; set; } = false;
    public bool IgnoreHTTPSErrors { get; set; } = false;
}

public class ViewportSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
}

public class TestSettings
{
    public string BaseUrl { get; set; } = "https://localhost:7118";
    public int WaitTimeout { get; set; } = 3000;
    public bool EnableScreenshots { get; set; } = true;
    public bool EnableVideoRecording { get; set; } = false;
}