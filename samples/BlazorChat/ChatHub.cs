using Hibira.AspNetCore;

namespace BlazorChat;

// The hub from Microsoft's SignalR + Blazor tutorial, on Hibira.
public class ChatHub : HibiraHub
{
    public async Task SendMessage(string user, string message) =>
        await Clients.All.SendAsync("ReceiveMessage", user, message);

    public async Task JoinRoom(string room)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, room);
        await Clients.Group(room).SendAsync("ReceiveMessage", "system", $"someone joined {room}");
    }
}
