using Microsoft.AspNetCore.SignalR;

namespace JoshHeaps.Net.Hubs;

public class ChessHub : Hub
{
    public async Task JoinWebsocketGroup(string gameId)
    {
        Console.WriteLine($"🔌 Joining group {gameId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
    }

    public async Task LeaveWebsocketGroup(string gameId)
    {
        Console.WriteLine($"❌ Leaving group {gameId}");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
    }
}
