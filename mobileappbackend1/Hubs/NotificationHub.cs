using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace mobileappbackend1.Hubs
{
    [Authorize]
    public class NotificationHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{Context.UserIdentifier}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{Context.UserIdentifier}");
            await base.OnDisconnectedAsync(exception);
        }
    }
}
