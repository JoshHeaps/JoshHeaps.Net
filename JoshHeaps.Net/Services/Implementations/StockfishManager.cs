using JoshHeaps.Net.DAL;
using JoshHeaps.Net.Hubs;
using JoshHeaps.Net.Models;
using JoshHeaps.Net.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace JoshHeaps.Net.Services.Implementations;

public class StockfishManager(IHubContext<ChessHub> chessHub, IChessService chessService, ChessDbAccess dbAccess)
{
    private readonly List<Stockfish> _workers = [];

    public async Task<bool> Run(GameState state)
    {
        if (!state.IsVsComputer)
            return true;

        if (!_workers.Any(x => x.InUse) && _workers.Count < 5)
            _workers.Add(new Stockfish(state.ComputerDifficulty));

        var worker = _workers.First(x => !x.InUse);

        worker.InUse = true;

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return await worker.MakeMove(state, chessHub, chessService, dbAccess);
        }
        finally
        {
            worker.InUse = false;
        }
    }

    public async Task<IEnumerable<KeyValuePair<Guid, GameState>>> RunDeliquents(ConcurrentDictionary<Guid, GameState> deliquents)
    {
        ConcurrentDictionary<Guid, GameState> continuedDeliquents = [];
        List<Task> tasks = [];

        foreach (var deliquent in deliquents)
        {
            tasks.Add(Task.Run(async () =>
            {
                var state = deliquent.Value;

                bool result = false;

                if (state.IsVsComputer && state.ComputerColor == state.CurrentPlayer)
                    result = !(await Run(state));

                if (result)
                    continuedDeliquents[deliquent.Key] = deliquent.Value;
            }));
        }

        await Task.WhenAll(tasks);

        return continuedDeliquents;
    }
}
