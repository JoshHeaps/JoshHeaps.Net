using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
var configuration = builder.Configuration;

builder.Services.AddControllers();

builder.Services.AddSignalR();

builder.Services.AddHttpClient("BlogApi", client =>
{
    var baseUrl = configuration["BlogApi:BaseUrl"] ?? "https://media.joshheaps.net";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IBlogService, BlogService>();
builder.Services.AddSingleton<IChessService, ChessService>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();

builder.Services.Configure<ChessEngineOptions>(configuration.GetSection(ChessEngineOptions.SectionName));
builder.Services.AddSingleton<ILearnedWeightsStore, LearnedWeightsStore>();
builder.Services.AddSingleton<IChessEngineFactory, ChessEngineFactory>();
builder.Services.AddSingleton<IComputerMoveOrchestrator, ComputerMoveOrchestrator>();
builder.Services.AddSingleton<IGameStore, GameStore>();
builder.Services.AddSingleton<ISelfPlayCoordinator, SelfPlayCoordinator>();

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<AutoIpUpdateService>();

    // Continuously train the learned engine against Stockfish in the background. Toggle off
    // via ChessEngine:AutoTrain (env ChessEngine__AutoTrain=false) without a redeploy.
    if (configuration.GetValue($"{ChessEngineOptions.SectionName}:{nameof(ChessEngineOptions.AutoTrain)}", true))
        builder.Services.AddHostedService<AutoTrainingService>();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
        ctx.Context.Response.Headers.Append("Expires", "0");
    }
});

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.MapControllers();

app.MapHub<ChessHub>("/chessHub");

app.Run();