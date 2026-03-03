using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using mobileappbackend1.Services;

namespace mobileappbackend1.Hubs
{
    /// <summary>
    /// Real-time messaging hub.
    ///
    /// Connection: wss://host/hubs/chat?access_token={jwt}
    ///   — The JWT is read from the "access_token" query parameter because
    ///     browsers cannot set custom headers on WebSocket upgrades.
    ///   — JWT validation is identical to the REST endpoints (same key/issuer/audience).
    ///
    /// User identity: SignalR maps ClaimTypes.NameIdentifier → Context.UserIdentifier
    ///   automatically, so Clients.User(id) targets all connections of one user.
    ///
    /// Client methods pushed by the server:
    ///   "ReceiveMessage" (Message)  — a new message arrived for you
    ///   "MessageSent"   (Message)  — echo to the sender's other connections (multi-tab sync)
    /// </summary>
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly MessageService _messageService;

        public ChatHub(MessageService messageService)
        {
            _messageService = messageService;
        }

        /// <summary>
        /// Called by the client to send a message.
        ///
        /// Validations (throws HubException on failure — surfaces as a rejection to the caller):
        ///   1. No self-messaging.
        ///   2. Content is non-empty and ≤ 2 000 characters.
        ///   3. Caller and recipient have a direct trainer-athlete relationship.
        /// </summary>
        public async Task SendMessage(string recipientId, string content)
        {
            var senderId = Context.UserIdentifier!;

            // 1. No self-messaging
            if (senderId == recipientId)
                throw new HubException("You cannot send a message to yourself.");

            // 2. Content validation
            content = content?.Trim() ?? string.Empty;
            if (content.Length == 0)
                throw new HubException("Message cannot be empty.");
            if (content.Length > 2000)
                throw new HubException("Message cannot exceed 2 000 characters.");

            // 3. Relationship check — only trainer-athlete pairs may message each other
            if (!await _messageService.IsRelationshipValidAsync(senderId, recipientId))
                throw new HubException("You can only message your assigned trainer or athletes.");

            // Persist to MongoDB
            var message = await _messageService.SendAsync(senderId, recipientId, content);

            // Push to every active connection of the recipient
            await Clients.User(recipientId).SendAsync("ReceiveMessage", message);

            // Echo to every OTHER connection of the sender (multi-tab / multi-device sync).
            // The calling connection displays the message optimistically; only other tabs need updating.
            await Clients.OthersInGroup($"user_{senderId}").SendAsync("ReceiveMessage", message);
        }

        // ── Connection lifecycle ──────────────────────────────────────────────

        public override async Task OnConnectedAsync()
        {
            // Add the connection to a per-user group so we can target "other connections
            // of the same user" without overriding the built-in user provider.
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
