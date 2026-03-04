using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum TrainerRequestStatus { Pending, Accepted, Rejected }

    public class TrainerRequest
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public TrainerRequestStatus Status { get; set; } = TrainerRequestStatus.Pending;

        /// <summary>Optional message the athlete writes when sending the request.</summary>
        [MaxLength(500)]
        public string? AthleteNote { get; set; }

        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RespondedAt { get; set; }
    }
}
