using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
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
    private readonly int _skill;

    public Stockfish(int skill = 20, int hash = 256)
    {
        _skill = skill;
        string relativeFilePath = @"\Resources\stockfish-windows-x86-64-avx2.exe";
        string exePath = Assembly.GetExecutingAssembly().Location.Split(@"\bin\")[0] + relativeFilePath;

        Console.Write(exePath);

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

    public async Task<string> GetBestMoveAsync(string fen)
    {
        Send($"position fen {fen}");
        Send($"go depth {_skill}");
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

    public async Task MakeMove(GameState state, IHubContext<ChessHub> chessHub, IChessService chessService)
    {
        var move = await GetBestMoveAsync(state.ToFen());

        var moveDto = move.ToMoveDto(
            state,
            state.CurrentPlayer == PieceColor.White
                ? state.WhitePlayerId
                : state.BlackPlayerId);

        var result = chessService.MakeMove(state, moveDto);

        await chessHub.Clients.Group(state.GameId.ToString()).SendAsync("ReceiveMoveUpdate", state.GameId.ToString(), moveDto, result);
    }
}

public static class StockfishHelpers
{
    public static MoveDto ToMoveDto(
        this string uci,
        GameState gameState,
        Guid playerId)
    {
        int fCol = uci[0] - 'a', fRow = 7 - (uci[1] - '1');
        int tCol = uci[2] - 'a', tRow = 7 - (uci[3] - '1');

        var piece = gameState.Board[fRow, fCol]
                    ?? throw new Exception("No piece at source square");

        PieceType? promo = uci.Length == 5 ? uci[4] switch
        {
            'q' => PieceType.Queen,
            'r' => PieceType.Rook,
            'b' => PieceType.Bishop,
            'n' => PieceType.Knight,
            _ => null
        } : null;

        return new MoveDto
        {
            GameId = gameState.GameId,
            PlayerId = playerId,
            PieceId = piece.Id,
            TargetRow = tRow,
            TargetCol = tCol,
            PromotionChoice = promo,
            SourceCol = fCol,
            SourceRow = fRow,
        };
    }

    /// <summary>
    /// Convert a 2-D board array (rank 8 = row 0, file a = col 0) to a FEN string.
    /// Only piece placement + active colour + castling are computed; the rest use
    /// safe defaults (-, 0, 1).  That is all Stockfish needs.
    /// </summary>
    public static string ToFen(this GameState gs)
    {
        var sb = new StringBuilder(64);

        /* 1) piece placement */
        for (int row = 0; row < 8; row++)
        {
            int empty = 0;

            for (int col = 0; col < 8; col++)
            {
                var p = gs.Board[row, col];

                if (p is null)
                {
                    empty++;
                }
                else
                {
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(ToFenChar(p));                 // ← unchanged helper
                }
            }

            if (empty > 0) sb.Append(empty);
            if (row < 7) sb.Append('/');
        }

        /* 2) active colour */
        sb.Append(gs.CurrentPlayer == PieceColor.White ? " w " : " b ");

        /* 3) castling rights (from GameState flags) */
        sb.Append(GetCastlingFlags(gs));

        /* 4) en-passant target square */
        sb.Append(' ');
        sb.Append(gs.EnPassantTarget.HasValue
            ? Alg(gs.EnPassantTarget.Value)
            : "-");

        /* 5-6) half-move clock + full-move number  */
        int fullMoves = gs.MoveHistory.Count / 2 + 1;
        sb.Append(" 0 ").Append(fullMoves);

        return sb.ToString();
    }

    /* ---------- helpers ---------- */

    private static string GetCastlingFlags(GameState gs)
    {
        var flags = new StringBuilder(4);

        if (gs.WhiteCanCastleKingside) flags.Append('K');
        if (gs.WhiteCanCastleQueenside) flags.Append('Q');
        if (gs.BlackCanCastleKingside) flags.Append('k');
        if (gs.BlackCanCastleQueenside) flags.Append('q');

        return flags.Length == 0 ? "-" : flags.ToString();
    }

    private static string Alg(Position p)
    {
        char file = (char)('a' + p.Col);
        int rank = 8 - p.Row;
        return $"{file}{rank}";
    }

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
}