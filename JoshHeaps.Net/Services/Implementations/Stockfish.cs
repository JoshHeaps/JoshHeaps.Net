using JoshHeaps.Net.Models;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Channels;

namespace JoshHeaps.Net.Services.Implementations;

public sealed class Stockfish : IAsyncDisposable
{
    private readonly Process _p;
    private readonly StreamWriter _stdin;
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();

    public Stockfish(int skill = 20, int hash = 256)
    {
        string relativeFilePath = @"JoshHeaps.Net\Resources\stockfish-windows-x86-64-avx2.exe";
        string exePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!
                        .Split("JoshHeaps.Net")
                        .First() + relativeFilePath);

        _p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _p.Start();
        _stdin = _p.StandardInput;

        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _p.StandardOutput.ReadLineAsync()) is not null)
                await _stdout.Writer.WriteAsync(line);
        });

        Send("uci");
        WaitFor("uciok").GetAwaiter().GetResult();

        Send($"setoption name Skill Level value {skill}");
        Send($"setoption name Hash value {hash}");
        Send("isready");
        WaitFor("readyok").GetAwaiter().GetResult();
    }

    public async Task<string> GetBestMoveAsync(string fen, int millis = 1000)
    {
        Send($"position fen {fen}");
        Send($"go movetime {millis}");
        string? best = null;

        await foreach (var line in _stdout.Reader.ReadAllAsync())
        {
            if (line.StartsWith("bestmove"))
            {
                best = line.Split(' ')[1];
                break;
            }
        }
        return best ?? throw new InvalidOperationException("Engine returned no move");
    }

    private void Send(string cmd) => _stdin.WriteLine(cmd);

    private async Task WaitFor(string token)
    {
        await foreach (var line in _stdout.Reader.ReadAllAsync())
            if (line == token) break;
    }

    public async ValueTask DisposeAsync()
    {
        Send("quit");
        _stdin.Close();
        await _p.WaitForExitAsync();
        _p.Dispose();
    }
}

public static class StockfishHelpers
{
    /// <summary>
    /// Convert a 2-D board array (rank 8 = row 0, file a = col 0) to a FEN string.
    /// Only piece placement + active colour + castling are computed; the rest use
    /// safe defaults (-, 0, 1).  That is all Stockfish needs.
    /// </summary>
    public static string ToFen(
        ChessPiece?[,] board,
        PieceColor activeColour = PieceColor.White)
    {
        if (board.GetLength(0) != 8 || board.GetLength(1) != 8)
            throw new ArgumentException("Board must be 8×8.");

        var sb = new StringBuilder(64);

        // ----- 1) piece placement -----
        for (int rank = 0; rank < 8; rank++)
        {
            int empty = 0;

            for (int file = 0; file < 8; file++)
            {
                var p = board[rank, file];

                if (p is null)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(ToFenChar(p));
                }
            }

            if (empty > 0) sb.Append(empty);
            if (rank < 7) sb.Append('/');
        }

        // ----- 2) active colour -----
        sb.Append(activeColour == PieceColor.White ? " w " : " b ");

        // ----- 3) castling rights (simple check of corner rooks + kings) -----
        sb.Append(GetCastlingFlags(board));

        // ----- 4-6)  en-passant, half-move, full-move  -----
        sb.Append(" - 0 1");         // en-passant target; clocks

        return sb.ToString();
    }

    /* ---------- helpers ---------- */

    private static char ToFenChar(ChessPiece p) => p switch
    {
        { Type: PieceType.Pawn, Color: PieceColor.White } => 'P',
        { Type: PieceType.Pawn, Color: PieceColor.Black } => 'p',
        { Type: PieceType.Knight, Color: PieceColor.White } => 'N',
        { Type: PieceType.Knight, Color: PieceColor.Black } => 'n',
        { Type: PieceType.Bishop, Color: PieceColor.White } => 'B',
        { Type: PieceType.Bishop, Color: PieceColor.Black } => 'b',
        { Type: PieceType.Rook, Color: PieceColor.White } => 'R',
        { Type: PieceType.Rook, Color: PieceColor.Black } => 'r',
        { Type: PieceType.Queen, Color: PieceColor.White } => 'Q',
        { Type: PieceType.Queen, Color: PieceColor.Black } => 'q',
        { Type: PieceType.King, Color: PieceColor.White } => 'K',
        { Type: PieceType.King, Color: PieceColor.Black } => 'k',
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    private static string GetCastlingFlags(ChessPiece?[,] b)
    {
        // Fast lookup helpers
        ChessPiece? A1 = b[7, 0], H1 = b[7, 7], E1 = b[7, 4];
        ChessPiece? A8 = b[0, 0], H8 = b[0, 7], E8 = b[0, 4];

        var flags = new StringBuilder(4);

        if (E1 is { Type: PieceType.King, Color: PieceColor.White, HasMoved: false })
        {
            if (H1 is { Type: PieceType.Rook, Color: PieceColor.White, HasMoved: false }) flags.Append('K');
            if (A1 is { Type: PieceType.Rook, Color: PieceColor.White, HasMoved: false }) flags.Append('Q');
        }
        if (E8 is { Type: PieceType.King, Color: PieceColor.Black, HasMoved: false })
        {
            if (H8 is { Type: PieceType.Rook, Color: PieceColor.Black, HasMoved: false }) flags.Append('k');
            if (A8 is { Type: PieceType.Rook, Color: PieceColor.Black, HasMoved: false }) flags.Append('q');
        }

        return flags.Length == 0 ? "-" : flags.ToString();
    }
}