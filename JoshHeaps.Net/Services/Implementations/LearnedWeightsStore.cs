using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;

namespace JoshHeaps.Net.Services.Implementations;

/// <summary>
/// Managed facade over the native learned-weights model (see <see cref="ILearnedWeightsStore"/>).
/// On construction it points the native engine at the weights file; everything else delegates
/// to the shared native ABI in <see cref="CustomChessEngine"/>.
/// </summary>
public sealed class LearnedWeightsStore : ILearnedWeightsStore
{
    private const int Pieces = 6;       // Pawn..King
    private const int Squares = 64;
    private const int Features = 8;     // mobility N/B/R/Q, passed, isolated, doubled, king safety

    public string WeightsFilePath { get; }

    public LearnedWeightsStore(IHostEnvironment env)
    {
        WeightsFilePath = Path.Combine(env.ContentRootPath, "chess-data", "learned-weights.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(WeightsFilePath)!);
        CustomChessEngine.NativeMethods.learned_load(WeightsFilePath);
    }

    public nint CreateTrainer() => CustomChessEngine.NativeMethods.trainer_create();

    public void Record(nint trainer, string fen) =>
        CustomChessEngine.NativeMethods.trainer_record(trainer, fen);

    public void ApplyResult(nint trainer, PieceColor winner, double weight) =>
        CustomChessEngine.NativeMethods.trainer_apply(trainer, winner == PieceColor.White ? 0 : 1, weight);

    public void DestroyTrainer(nint trainer) =>
        CustomChessEngine.NativeMethods.trainer_destroy(trainer);

    public LearnedWeightsSnapshot Snapshot()
    {
        const int total = Pieces * Squares * 2 + Features;
        var buffer = new int[total];

        unsafe
        {
            fixed (int* p = buffer)
                CustomChessEngine.NativeMethods.weights_snapshot(p, total);
        }

        var mg = new int[Pieces][];
        var eg = new int[Pieces][];

        for (int piece = 0; piece < Pieces; piece++)
        {
            mg[piece] = new int[Squares];
            eg[piece] = new int[Squares];
            Array.Copy(buffer, piece * Squares, mg[piece], 0, Squares);
            Array.Copy(buffer, Pieces * Squares + piece * Squares, eg[piece], 0, Squares);
        }

        var features = new int[Features];
        Array.Copy(buffer, Pieces * Squares * 2, features, 0, Features);

        return new LearnedWeightsSnapshot(mg, eg, features);
    }
}
