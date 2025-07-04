using JoshHeaps.Net.Models;
using System.Text.Json;

namespace JoshHeaps.Net.DAL;

public class ChessDbAccess(ChessDbContext db)
{
    private static readonly JsonSerializerOptions opts =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task SaveAsync(GameState state, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(state, opts);
        var entity = await db.Games.FindAsync([state.GameId], ct)
                 ?? new GameStateEntity { GameId = state.GameId };

        entity.SerializedState = json;
        entity.LastMoveUtc = DateTime.UtcNow;

        db.Update(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<GameState?> LoadAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Games.FindAsync([id], ct);
        return e is null
            ? null
            : JsonSerializer.Deserialize<GameState>(e.SerializedState, opts);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (await db.Games.FindAsync([id], ct) is { } e)
        {
            db.Remove(e);
            await db.SaveChangesAsync(ct);
        }
    }
}
