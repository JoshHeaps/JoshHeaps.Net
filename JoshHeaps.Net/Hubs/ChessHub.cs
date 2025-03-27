using JoshHeaps.Net.Models;
using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Hubs;

public class ChessHub : Hub
{
    public async Task JoinWebsocketGroup(string gameId)
    {
        Console.WriteLine($"🔌 Joining group {gameId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
    }

    public async Task MoveMade(string gameId, MoveResultDto moveResult)
    {
        await Clients.OthersInGroup(gameId).SendAsync("ReceiveMoveUpdate", gameId, moveResult);
    }
}
