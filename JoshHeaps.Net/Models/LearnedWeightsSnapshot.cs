namespace JoshHeaps.Net.Models;

/// <summary>
/// A copy of the learned engine's weights for display: midgame and endgame piece-square
/// tables (one 64-entry array per piece, in canonical Pawn..King order, white-relative
/// A1=0..H8=63) plus the feature weights (mobility N/B/R/Q, passed, isolated, doubled,
/// king safety).
/// </summary>
public sealed record LearnedWeightsSnapshot(int[][] Mg, int[][] Eg, int[] Features);
