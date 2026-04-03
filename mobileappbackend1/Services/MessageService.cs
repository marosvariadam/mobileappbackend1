using mobileappbackend1.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class ConversationInfo
    {
        public string OtherUserId { get; set; } = string.Empty;
        public string LastMessageContent { get; set; } = string.Empty;
        public DateTime LastSentAt { get; set; }
        public int UnreadCount { get; set; }
    }

    public class MessageService
    {
        private readonly IMongoCollection<Message> _messages;
        private readonly IMongoCollection<User> _users;

        public MessageService(IMongoDatabase database)
        {
            _messages = database.GetCollection<Message>("Messages");
            _users    = database.GetCollection<User>("Users");
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        public static string BuildConversationId(string userId1, string userId2)
        {
            var ordered = new[] { userId1, userId2 }.OrderBy(x => x).ToArray();
            return $"{ordered[0]}_{ordered[1]}";
        }

        public async Task<bool> IsRelationshipValidAsync(string userId1, string userId2)
        {
            if (string.IsNullOrEmpty(userId1) || string.IsNullOrEmpty(userId2)) return false;
            if (userId1 == userId2) return false;

            var user = await _users.Find(u => u.Id == userId1).FirstOrDefaultAsync();
            if (user == null) return false;

            if (user.Role == UserRole.Athlete)
                return user.TrainerId == userId2;

            var other = await _users.Find(u => u.Id == userId2).FirstOrDefaultAsync();
            return other?.Role == UserRole.Athlete && other.TrainerId == userId1;
        }

        // ── Write ─────────────────────────────────────────────────────────────

        public async Task<Message> SendAsync(string senderId, string recipientId, string content)
        {
            var message = new Message
            {
                ConversationId = BuildConversationId(senderId, recipientId),
                SenderId       = senderId,
                RecipientId    = recipientId,
                Content        = content,
                CreatedAt      = DateTime.UtcNow,
                IsRead         = false,
                IsDeleted      = false
            };

            await _messages.InsertOneAsync(message);
            return message;
        }

        // ── Read ──────────────────────────────────────────────────────────────

        public async Task<List<Message>> GetConversationAsync(
            string userId, string otherId, int page = 1, int pageSize = 50)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            var convId = BuildConversationId(userId, otherId);

            var filter = Builders<Message>.Filter.And(
                Builders<Message>.Filter.Eq(m => m.ConversationId, convId),
                Builders<Message>.Filter.Ne(m => m.IsDeleted, true));

            return await _messages
                .Find(filter)
                .SortBy(m => m.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();
        }

        public async Task<List<ConversationInfo>> GetConversationsAsync(string userId)
        {
            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument
                {
                    { "IsDeleted", new BsonDocument("$ne", true) },
                    { "$or", new BsonArray
                    {
                        new BsonDocument("SenderId",    userId),
                        new BsonDocument("RecipientId", userId)
                    }}
                }),
                new BsonDocument("$sort", new BsonDocument("SentAt", -1)),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id",             "$ConversationId" },
                    { "LastContent",     new BsonDocument("$first", "$Content") },
                    { "LastSentAt",      new BsonDocument("$first", "$SentAt") },
                    { "OtherUserId",     new BsonDocument("$first",
                        new BsonDocument("$cond", new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { "$SenderId", userId }),
                            "$RecipientId",
                            "$SenderId"
                        })) },
                    { "UnreadCount",     new BsonDocument("$sum",
                        new BsonDocument("$cond", new BsonArray
                        {
                            new BsonDocument("$and", new BsonArray
                            {
                                new BsonDocument("$eq", new BsonArray { "$RecipientId", userId }),
                                new BsonDocument("$eq", new BsonArray { "$IsRead",      false   })
                            }),
                            1,
                            0
                        })) }
                }),
                new BsonDocument("$sort", new BsonDocument("LastSentAt", -1))
            };

            var results = await _messages
                .Aggregate<BsonDocument>(pipeline)
                .ToListAsync();

            return results.Select(r => new ConversationInfo
            {
                OtherUserId          = r["OtherUserId"].AsString,
                LastMessageContent   = r["LastContent"].AsString,
                LastSentAt           = r["LastSentAt"].ToUniversalTime(),
                UnreadCount          = r["UnreadCount"].AsInt32
            }).ToList();
        }

        /// <summary>
        /// Returns the total number of unread messages across all conversations for a user.
        /// </summary>
        public async Task<int> GetTotalUnreadCountAsync(string userId)
        {
            var unreadFilter = Builders<Message>.Filter.And(
                Builders<Message>.Filter.Eq(m => m.RecipientId, userId),
                Builders<Message>.Filter.Eq(m => m.IsRead, false),
                Builders<Message>.Filter.Ne(m => m.IsDeleted, true));

            return (int)await _messages.CountDocumentsAsync(unreadFilter);
        }

        // ── Update ────────────────────────────────────────────────────────────

        public async Task MarkConversationAsReadAsync(string currentUserId, string otherId)
        {
            var convId = BuildConversationId(currentUserId, otherId);

            var filter = Builders<Message>.Filter.And(
                Builders<Message>.Filter.Eq(m => m.ConversationId, convId),
                Builders<Message>.Filter.Eq(m => m.RecipientId,    currentUserId),
                Builders<Message>.Filter.Eq(m => m.IsRead,         false));

            await _messages.UpdateManyAsync(
                filter,
                Builders<Message>.Update.Set(m => m.IsRead, true));
        }

        // ── Delete ────────────────────────────────────────────────────────────

        /// <summary>
        /// Soft-deletes a message. Only the sender can delete their own messages.
        /// Returns the message if deleted, null if not found or not the sender.
        /// </summary>
        public async Task<Message?> DeleteMessageAsync(string messageId, string senderId)
        {
            var deleteFilter = Builders<Message>.Filter.And(
                Builders<Message>.Filter.Eq(m => m.Id, messageId),
                Builders<Message>.Filter.Eq(m => m.SenderId, senderId),
                Builders<Message>.Filter.Ne(m => m.IsDeleted, true));

            var update = Builders<Message>.Update.Set(m => m.IsDeleted, true);

            return await _messages.FindOneAndUpdateAsync(deleteFilter, update);
        }
    }
}
