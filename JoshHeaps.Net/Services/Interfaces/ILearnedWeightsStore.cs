using JoshHeaps.Net.Models;

namespace JoshHeaps.Net.Services.Interfaces;

/// <summary>
/// Thin managed facade over the native learned-weights model. The weights, all feature
/// computation, per-game accumulation, the update rule, and persistence live in the native
/// engine; this just points it at the weights file, hands out per-game trainers, and reads
/// the table back for visualization.
/// </summary>
public interface ILearnedWeightsStore
{
    /// <summary>Absolute path to the weights file the native engine loads and saves.</summary>
    string WeightsFilePath { get; }

    /// <summary>A copy of the current weights (midgame/endgame tables + feature weights).</summary>
    LearnedWeightsSnapshot Snapshot();

    /// <summary>Creates a per-game training accumulator. The caller owns it (see <see cref="DestroyTrainer"/>).</summary>
    nint CreateTrainer();

    /// <summary>Records one played position (post-move FEN) into a trainer.</summary>
    void Record(nint trainer, string fen);

    /// <summary>
    /// Applies a finished game's outcome to the global weights (and saves): rewards the
    /// winner's squares/features, punishes the loser's, scaled by <paramref name="weight"/>.
    /// </summary>
    void ApplyResult(nint trainer, PieceColor winner, double weight);

    /// <summary>Frees a trainer. Safe to call with <see cref="nint.Zero"/>.</summary>
    void DestroyTrainer(nint trainer);
}
