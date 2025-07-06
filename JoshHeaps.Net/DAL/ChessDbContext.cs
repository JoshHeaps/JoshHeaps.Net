using JoshHeaps.Net.Models;
using Microsoft.EntityFrameworkCore;

namespace JoshHeaps.Net.DAL;

public class ChessDbContext : DbContext
{
    public ChessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChessDbContext>()
            .UseSqlite("Data Source=Data/chess.db")   // same conn-string as runtime
            .Options;

        return new ChessDbContext(options);
    }

    public ChessDbContext(DbContextOptions<ChessDbContext> opts) : base(opts) { }

    public DbSet<GameStateEntity> Games => Set<GameStateEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Primary key
        b.Entity<GameStateEntity>().HasKey(g => g.GameId);

        // Board + pieces serialized as JSON blobs
        b.Entity<GameStateEntity>()
            .Property(g => g.SerializedState)
            .HasColumnType("TEXT");
    }
}

