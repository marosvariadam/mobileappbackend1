using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public class Message
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        /// <summary>
        /// Deterministic ID shared by both participants:
        /// the two user ObjectIds sorted lexicographically and joined with "_".
        /// Generated server-side — never trusted from the client.
        /// </summary>
        public string ConversationId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string SenderId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string RecipientId { get; set; } = string.Empty;

        /// <summary>Max 2 000 characters, enforced at the service and DTO layers.</summary>
        public string Content { get; set; } = string.Empty;

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        public bool IsRead { get; set; } = false;
    }
}
