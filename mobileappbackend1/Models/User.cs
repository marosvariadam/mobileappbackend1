using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{

    public enum UserRole { Trainer, Athlete}

    public class User
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }
        
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }

        [BsonRepresentation(BsonType.String)]
        public UserRole Role { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        
        public string? TrainerId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
