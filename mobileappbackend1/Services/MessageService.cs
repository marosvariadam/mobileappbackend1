using mobileappbackend1.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    /// <summary>Summary of one conversation returned in the conversation list.</summary>
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

        /// <summary>
        /// Builds the deterministic conversation ID for two users.
        /// Sorting ensures both sides always produce the same ID regardless of who queries first.
        /// </summary>
        public static string BuildConversationId(string userId1, string userId2)
        {
            var ordered = new[] { userId1, userId2 }.OrderBy(x => x).ToArray();
            return $"{ordered[0]}_{ordered[1]}";
        }

        /// <summary>
        /// Returns true only if the two users have a direct trainer-athlete relationship.
        /// Trainers can message their own athletes; athletes can only message their assigned trainer.
        /// Self-messaging is always rejected.
        /// </summary>
        public async Task<bool> IsRelationshipValidAsync(string userId1, string userId2)
        {
            if (string.IsNullOrEmpty(userId1) || string.IsNullOrEmpty(userId2)) return false;
            if (userId1 == userId2) return false;

            var user = await _users.Find(u => u.Id == userId1).FirstOrDefaultAsync();
            if (user == null) return false;

            if (user.Role == UserRole.Athlete)
                // Athlete may only message their assigned trainer
                return user.TrainerId == userId2;

            // Trainer may message any of their athletes
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
                SentAt         = DateTime.UtcNow,
                IsRead         = false
            };

            await _messages.InsertOneAsync(message);
            return message;
        }

        // ── Read ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns paged message history between two users, newest first.
        /// Ownership is guaranteed: ConversationId is built from the caller's JWT userId.
        /// </summary>
        public async Task<List<Message>> GetConversationAsync(
            string userId, string otherId, int page = 1, int pageSize = 50)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            var convId = BuildConversationId(userId, otherId);

            return await _messages
                .Find(m => m.ConversationId == convId)
                .SortByDescending(m => m.SentAt)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();
        }

        /// <summary>
        /// Returns the caller's conversation list via a MongoDB aggregation pipeline,
        /// showing the last message and unread count per conversation.
        /// </summary>
        public async Task<List<ConversationInfo>> GetConversationsAsync(string userId)
        {
            // Pipeline:
            // 1. Match messages where the caller is sender or recipient.
            // 2. Sort newest-first so $first picks the latest message per conversation.
            // 3. Group by ConversationId to get last message + unread count.
            // 4. Re-sort the resulting conversation list newest-first.
            var pipeline = new[]
            {
                new BsonDocument("$match", new BsonDocument("$or", new BsonArray
                {
                    new BsonDocument("SenderId",    userId),
                    new BsonDocument("RecipientId", userId)
                })),
                new BsonDocument("$sort", new BsonDocument("SentAt", -1)),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id",             "$ConversationId" },
                    { "LastContent",     new BsonDocument("$first", "$Content") },
                    { "LastSentAt",      new BsonDocument("$first", "$SentAt") },
                    // OtherUserId: the participant that is NOT the caller
                    { "OtherUserId",     new BsonDocument("$first",
                        new BsonDocument("$cond", new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { "$SenderId", userId }),
                            "$RecipientId",
                            "$SenderId"
                        })) },
                    // UnreadCount: messages addressed TO the caller that are unread
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

        // ── Update ────────────────────────────────────────────────────────────

        /// <summary>
        /// Marks all messages sent TO the current user in a given conversation as read.
        /// Only the recipient can mark messages as read — enforced by the RecipientId filter.
        /// </summary>
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
    }
}
