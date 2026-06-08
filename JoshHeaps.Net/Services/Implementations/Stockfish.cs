using JoshHeaps.Net.Services.Interfaces;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace JoshHeaps.Net.Services.Implementations;

public sealed class Stockfish : IChessEngine
{
    private readonly Process _p;
    private readonly StreamWriter _stdin;
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly int _skill;

    public int Skill => _skill;

    public Stockfish(int skill = 20, int hash = 256)
    {
        _skill = skill;
        string baseDir = AppContext.BaseDirectory;                // always points to the folder that holds your DLL / EXE
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                  ? "stockfish-windows-x86-64-avx2.exe"
                  : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "stockfish-mac-x86-64-avx2"
                    : "stockfish-ubuntu-x86-64-sse41-popcnt";   // default: Linux

        string exePath = Path.Combine(baseDir, "Resources", fileName);

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"Stockfish executable not found at {exePath}. " +
                "Ensure the file is present in the Resources folder of your project.");
        }

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

        try
        {
            _p.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting process: {ex.Message}");
            throw;
        }

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
}
