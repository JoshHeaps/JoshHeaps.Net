using JoshHeaps.Net.DAL;
using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Services.Implementations;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
var configuration = builder.Configuration;

builder.Services.AddControllers();

builder.Services.AddSignalR();

builder.Services.AddSingleton<IChessService, ChessService>();
builder.Services.AddSingleton<BackgroundTaskQueue>();
builder.Services.AddSingleton<ChessDbAccess>();
builder.Services.AddSingleton<StockfishManager>();

var cs = builder.Configuration.GetConnectionString("ChessDatabase");

builder.Services.AddPooledDbContextFactory<ChessDbContext>(o => o.UseSqlite(cs));
builder.Services.AddHostedService<GameCleanupService>();

if (!builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<AutoIpUpdateService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ChessDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    db.Database.Migrate();
}

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
