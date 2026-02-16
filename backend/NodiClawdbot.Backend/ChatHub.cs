using Microsoft.AspNetCore.SignalR;

namespace NodiClawdbot.Backend;

public sealed class ChatHub : Hub
{
    public async Task Send(string message)
    {
        var trimmed = (message ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;

        await Clients.All.SendAsync("message", new
        {
            at = DateTimeOffset.UtcNow,
            from = Context.ConnectionId,
            text = trimmed,
        });
    }
}
