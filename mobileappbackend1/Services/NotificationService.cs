using Microsoft.AspNetCore.SignalR;
using mobileappbackend1.Hubs;
using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class NotificationService
    {
        private readonly IMongoCollection<Notification> _notifications;
        private readonly IHubContext<ChatHub> _hub;

        public NotificationService(IMongoDatabase database, IHubContext<ChatHub> hub)
        {
            _notifications = database.GetCollection<Notification>("Notifications");
            _hub = hub;
        }

        /// <summary>
        /// Persists a notification and immediately pushes it to all active connections
        /// of the recipient via SignalR. Works even if the recipient is offline —
        /// the document is stored and the client fetches it on next GET /api/notification.
        /// </summary>
        public async Task<Notification> CreateAndSendAsync(
            string userId, NotificationType type,
            string title, string body,
            string? referenceId = null)
        {
            var notification = new Notification
            {
                UserId      = userId,
                Type        = type,
                Title       = title,
                Body        = body,
                ReferenceId = referenceId,
                IsRead      = false,
                CreatedAt   = DateTime.UtcNow
            };

            await _notifications.InsertOneAsync(notification);

            // Push real-time — fires-and-forgets gracefully if the user is offline
            await _hub.Clients.User(userId).SendAsync("Notification", notification);

            return notification;
        }

        public async Task<List<Notification>> GetForUserAsync(
            string userId, int page = 1, int pageSize = 30)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _notifications
                .Find(n => n.UserId == userId)
                .SortByDescending(n => n.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();
        }

        public async Task<long> GetUnreadCountAsync(string userId)
        {
            return await _notifications.CountDocumentsAsync(
                n => n.UserId == userId && !n.IsRead);
        }

        public async Task MarkAsReadAsync(string userId, string notificationId)
        {
            await _notifications.UpdateOneAsync(
                n => n.Id == notificationId && n.UserId == userId,
                Builders<Notification>.Update.Set(n => n.IsRead, true));
        }

        public async Task MarkAllAsReadAsync(string userId)
        {
            await _notifications.UpdateManyAsync(
                n => n.UserId == userId && !n.IsRead,
                Builders<Notification>.Update.Set(n => n.IsRead, true));
        }
    }
}
