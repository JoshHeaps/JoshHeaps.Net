using JoshHeaps.Net.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>The available chess engine implementations.</summary>
public enum ChessEngineKind
{
    Stockfish,
    Custom,

    /// <summary>The custom engine with the reinforcement-learned piece-square evaluation.</summary>
    CustomLearned
}

/// <summary>Configuration selecting which <see cref="IChessEngine"/> to use.</summary>
public sealed class ChessEngineOptions
{
    public const string SectionName = "ChessEngine";

    public ChessEngineKind Engine { get; set; } = ChessEngineKind.Stockfish;

    /// <summary>
    /// Absolute path to the learned-weights file. Leave null to default to
    /// <c>{ContentRoot}/chess-data/learned-weights.txt</c> (fine for local dev). In
    /// production set this to a stable, service-writable location OUTSIDE the deploy
    /// directory (e.g. <c>/var/lib/joshheaps/chess-data/learned-weights.txt</c>) so the
    /// trained weights survive deploys and avoid deploy-user vs service-user permission
    /// clashes. Override via the <c>ChessEngine__WeightsPath</c> environment variable.
    /// </summary>
    public string? WeightsPath { get; set; }

    /// <summary>
    /// When true (and outside Development), a background service continuously plays the learned
    /// engine against Stockfish to train it. Set to false to stop auto-training without a
    /// redeploy. Override via the <c>ChessEngine__AutoTrain</c> environment variable.
    /// </summary>
    public bool AutoTrain { get; set; } = true;
}

/// <summary>Creates the configured <see cref="IChessEngine"/> per game.</summary>
public sealed class ChessEngineFactory(
    IOptions<ChessEngineOptions> options,
    ILearnedWeightsStore weightsStore) : IChessEngineFactory
{
    private readonly ChessEngineKind _kind = options.Value.Engine;

    public IChessEngine Create(int skill) => Create(skill, _kind);

    public IChessEngine Create(int skill, ChessEngineKind kind) => kind switch
    {
        ChessEngineKind.Custom => new CustomChessEngine(skill),
        ChessEngineKind.CustomLearned => new CustomChessEngine(skill, EngineVariant.Learned, weightsStore.WeightsFilePath),
        ChessEngineKind.Stockfish => new Stockfish(skill),
        _ => throw new InvalidOperationException($"Unknown chess engine '{kind}'.")
    };
}
