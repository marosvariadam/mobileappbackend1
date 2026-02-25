using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum UserRole { Trainer, Athlete }

    public class User
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string LastName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string Email { get; set; } = string.Empty;

        // Never serialized to JSON responses; stored as BCrypt hash in DB
        [JsonIgnore]
        public string PasswordHash { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public UserRole Role { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string? TrainerId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Refresh token support (stored as SHA-256 hash)
        [JsonIgnore]
        public string? RefreshTokenHash { get; set; }

        [JsonIgnore]
        public DateTime? RefreshTokenExpiry { get; set; }
    }
}
