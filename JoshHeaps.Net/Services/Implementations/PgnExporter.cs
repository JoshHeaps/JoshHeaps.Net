using JoshHeaps.Net.Models;
using System.Text;

namespace JoshHeaps.Net.Services.Implementations;

public static class PgnExporter
{
    /// <summary>
    /// Render a game as PGN: the standard seven-tag header plus the SAN movetext and result.
    /// </summary>
    public static string ToPgn(this GameState gs)
    {
        var result = ResultTag(gs);
        var name = gs.IsComputerVsComputer ? "Computer" : null;

        var sb = new StringBuilder();
        sb.AppendLine("[Event \"JoshHeaps.Net Chess\"]");
        sb.AppendLine("[Site \"joshheaps.net\"]");
        sb.AppendLine($"[Date \"{DateTime.Now:yyyy.MM.dd}\"]");
        sb.AppendLine($"[White \"{name ?? "White"}\"]");
        sb.AppendLine($"[Black \"{name ?? "Black"}\"]");
        sb.AppendLine($"[Result \"{result}\"]");
        sb.AppendLine();

        for (int i = 0; i < gs.SanHistory.Count; i++)
        {
            if (i % 2 == 0)
                sb.Append(i / 2 + 1).Append(". ");

            sb.Append(gs.SanHistory[i]).Append(' ');
        }

        sb.Append(result);
        return sb.ToString();
    }

    private static string ResultTag(GameState gs)
    {
        if (gs.IsCheckmate)
            return gs.CurrentPlayer == PieceColor.White ? "0-1" : "1-0";

        if (gs.IsStalemate || gs.IsThreefoldRepetition)
            return "1/2-1/2";

        if (gs.IsForfeited)
            return gs.Winner == PieceColor.White ? "1-0" : "0-1";

        return "*";
    }
}
