using JoshHeaps.Net.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>The available chess engine implementations.</summary>
public enum ChessEngineKind
{
    Stockfish,
    Custom
}

/// <summary>Configuration selecting which <see cref="IChessEngine"/> to use.</summary>
public sealed class ChessEngineOptions
{
    public const string SectionName = "ChessEngine";

    public ChessEngineKind Engine { get; set; } = ChessEngineKind.Stockfish;
}

/// <summary>Creates the configured <see cref="IChessEngine"/> per game.</summary>
public sealed class ChessEngineFactory(IOptions<ChessEngineOptions> options) : IChessEngineFactory
{
    private readonly ChessEngineKind _kind = options.Value.Engine;

    public IChessEngine Create(int skill) => _kind switch
    {
        ChessEngineKind.Custom => new CustomChessEngine(skill),
        ChessEngineKind.Stockfish => new Stockfish(skill),
        _ => throw new InvalidOperationException($"Unknown chess engine '{_kind}'.")
    };
}
