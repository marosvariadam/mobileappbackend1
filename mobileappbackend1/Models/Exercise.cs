using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public class Exercise
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string Description { get; set; } = string.Empty;

        [MaxLength(100)]
        public string MuscleGroup { get; set; } = string.Empty;

        [MaxLength(200)]
        public string Equipment { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string? CreatedByTrainerId { get; set; }
    }
}
