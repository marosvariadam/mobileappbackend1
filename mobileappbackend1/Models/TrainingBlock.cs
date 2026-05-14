using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    /// <summary>
    /// A date-ranged "training block" a trainer assigns to an athlete, describing
    /// the program phase (Push, Pull, Legs, Upper, Lower, Full, etc.). The
    /// feature-engineering pipeline uses the focus of the block covering a given
    /// week as the <c>Focus</c> feature for that week's rows.
    ///
    /// Blocks for the same athlete must not overlap; weeks uncovered by any block
    /// default to <c>"Full"</c> at feature-gen time.
    /// </summary>
    public class TrainingBlock
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string Focus { get; set; } = string.Empty;

        [Required]
        public DateTime StartDate { get; set; }

        [Required]
        public DateTime EndDate { get; set; }

        [MaxLength(1000)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
