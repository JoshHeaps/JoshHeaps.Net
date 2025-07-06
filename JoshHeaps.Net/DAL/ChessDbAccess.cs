using JoshHeaps.Net.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace JoshHeaps.Net.DAL;

public class ChessDbAccess(IDbContextFactory<ChessDbContext> factory)
{
    private static readonly JsonSerializerOptions opts =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task SaveAsync(GameState state, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var json = JsonSerializer.Serialize(state, opts);
        var entity = await db.Games.FindAsync([state.GameId], ct);

        if (entity is null)
        {
            entity = new GameStateEntity { GameId = state.GameId };
            db.Games.Add(entity);                 // INSERT path
        }
        else
        {
            db.Games.Update(entity);              // UPDATE path
        }

        entity.SerializedState = json;
        entity.LastMoveUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task<GameState?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var e = await db.Games.FindAsync([id], ct);
        return e is null
            ? null
            : JsonSerializer.Deserialize<GameState>(e.SerializedState, opts);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        if (await db.Games.FindAsync([id], ct) is { } e)
        {
            db.Remove(e);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteAllAsync(Func<GameState, bool>? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        filter ??= _ => true;

        // 1.  Pull every row (just once, not per request)
        var entities = await db.Games
            .AsNoTracking()          // no change tracking needed
            .Select(g => g.SerializedState)
            .ToListAsync(ct);

        List<GameState> allGames = entities.Select(json => JsonSerializer.Deserialize<GameState>(json, opts))
            .OfType<GameState>()
            .Where(filter)
            .ToList();

        foreach (var game in allGames)
        {
            await DeleteAsync(game.GameId, ct);
        }
    }

    public async Task<List<GameState>> LoadAllAsync(Func<GameState, bool>? filter = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        filter ??= _ => true;

        // 1.  Pull every row (just once, not per request)
        var entities = await db.Games
            .AsNoTracking()          // no change tracking needed
            .Select(g => g.SerializedState)
            .ToListAsync(ct);

        List<GameState> allGames = entities.Select(json => JsonSerializer.Deserialize<GameState>(json, opts))
            .OfType<GameState>()
            .Where(filter)
            .ToList();

        return allGames;
    }
}
