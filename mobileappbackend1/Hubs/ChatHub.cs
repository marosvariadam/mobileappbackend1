using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using mobileappbackend1.Services;

namespace mobileappbackend1.Hubs
{
    /// <summary>
    /// Real-time messaging hub.
    ///
    /// Connection: wss://host/hubs/chat?access_token={jwt}
    ///
    /// Client methods pushed by the server:
    ///   "ReceiveMessage"   (Message)                    - a new message arrived
    ///   "MessageDeleted"   ({ conversationId, messageId }) - a message was deleted
    ///   "MessagesRead"     ({ conversationId, readByUserId }) - messages were marked as read
    ///   "UserTyping"       ({ userId })                 - other user started typing
    ///   "UserStoppedTyping"({ userId })                 - other user stopped typing
    ///   "UserOnline"       ({ userId })                 - a user came online
    ///   "UserOffline"      ({ userId })                 - a user went offline
    /// </summary>
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly MessageService _messageService;
        private readonly PresenceTracker _presenceTracker;

        public ChatHub(MessageService messageService, PresenceTracker presenceTracker)
        {
            _messageService = messageService;
            _presenceTracker = presenceTracker;
        }


        public async Task SendMessage(string recipientId, string content)
        {
            var senderId = Context.UserIdentifier!;

            if (senderId == recipientId)
                throw new HubException("You cannot send a message to yourself.");

            content = content?.Trim() ?? string.Empty;
            if (content.Length == 0)
                throw new HubException("Message cannot be empty.");
            if (content.Length > 2000)
                throw new HubException("Message cannot exceed 2 000 characters.");

            if (!await _messageService.IsRelationshipValidAsync(senderId, recipientId))
                throw new HubException("You can only message your assigned trainer or athletes.");

            var message = await _messageService.SendAsync(senderId, recipientId, content);

            await Clients.User(recipientId).SendAsync("ReceiveMessage", message);
            await Clients.OthersInGroup($"user_{senderId}").SendAsync("ReceiveMessage", message);
        }


        public async Task StartTyping(string recipientId)
        {
            var senderId = Context.UserIdentifier!;
            if (senderId == recipientId) return;

            await Clients.User(recipientId).SendAsync("UserTyping", new { userId = senderId });
        }

        public async Task StopTyping(string recipientId)
        {
            var senderId = Context.UserIdentifier!;
            if (senderId == recipientId) return;

            await Clients.User(recipientId).SendAsync("UserStoppedTyping", new { userId = senderId });
        }


        public async Task MarkAsRead(string otherId)
        {
            var userId = Context.UserIdentifier!;
            if (userId == otherId) return;

            await _messageService.MarkConversationAsReadAsync(userId, otherId);

            var conversationId = MessageService.BuildConversationId(userId, otherId);

            // Notify the other user that their messages were read
            await Clients.User(otherId).SendAsync("MessagesRead", new
            {
                conversationId,
                readByUserId = userId
            });
        }


        public async Task DeleteMessage(string messageId)
        {
            var userId = Context.UserIdentifier!;

            var message = await _messageService.DeleteMessageAsync(messageId, userId);
            if (message == null)
                throw new HubException("Message not found or you are not the sender.");

            var payload = new { conversationId = message.ConversationId, messageId };

            // Notify recipient
            await Clients.User(message.RecipientId).SendAsync("MessageDeleted", payload);
            // Notify sender's other connections
            await Clients.OthersInGroup($"user_{userId}").SendAsync("MessageDeleted", payload);
        }


        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier!;
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");

            var isFirstConnection = _presenceTracker.UserConnected(userId, Context.ConnectionId);
            if (isFirstConnection)
            {
                // Broadcast to all OTHER authenticated users that this user came online
                await Clients.Others.SendAsync("UserOnline", new { userId });
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.UserIdentifier!;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"user_{userId}");

            var isLastConnection = _presenceTracker.UserDisconnected(userId, Context.ConnectionId);
            if (isLastConnection)
            {
                await Clients.Others.SendAsync("UserOffline", new { userId });
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Called by the client after connecting to get the list of currently online users.
        /// </summary>
        public List<string> GetOnlineUsers()
        {
            return _presenceTracker.GetOnlineUsers();
        }
    }
}
