using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum Goal
    {
        Hypertrophy,
        Strength,
        GeneralFitness,
        FatLoss
    }

    // Structured, machine-readable profile used by the plan generator.
    // The free-text OnboardingForm answers stay as the trainer-authored
    // intake form; this document is the normalized subset.
    public class AthleteProfile
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public Goal Goal { get; set; } = Goal.GeneralFitness;

        [BsonRepresentation(BsonType.String)]
        public ExperienceLevel ExperienceLevel { get; set; } = ExperienceLevel.Beginner;

        [Range(2, 6)]
        public int DaysPerWeek { get; set; } = 3;

        [Range(30, 120)]
        public int SessionMinutes { get; set; } = 60;

        public List<EquipmentType> AvailableEquipment { get; set; } = new();

        // e.g. "knee", "lower_back" - matched against Exercise.StressTags.
        public List<string> AvoidStressTags { get; set; } = new();

        [BsonRepresentation(BsonType.ObjectId)]
        public List<string> ExcludedExerciseIds { get; set; } = new();

        // Per-muscle-group priority multiplier used by the scorer; 0.5-1.5,
        // default 1.0 for unlisted muscle groups.
        public Dictionary<string, double> MuscleGroupPriority { get; set; } = new();

        public double? WeightKg { get; set; }

        [MaxLength(2000)]
        public string? SportHistory { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }
}
