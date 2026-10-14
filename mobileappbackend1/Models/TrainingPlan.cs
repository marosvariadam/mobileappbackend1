using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum SplitType { FullBody, UpperLower, PushPullLegs, Custom }

    public enum PlanStatus { Draft, Accepted, Materialized, Archived }

    public enum ProgressionScheme { Linear, DoubleProgression, RpeBased }

    public enum GenerationSource { Constraint, LlmOnly, Hybrid, Manual }

    public class TrainingPlan
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

        [BsonRepresentation(BsonType.ObjectId)]
        public string? TrainingBlockId { get; set; }

        [Required]
        [MaxLength(300)]
        public string Title { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public Goal Goal { get; set; } = Goal.GeneralFitness;

        [Range(1, 52)]
        public int Weeks { get; set; } = 4;

        [BsonRepresentation(BsonType.String)]
        public SplitType Split { get; set; } = SplitType.FullBody;

        [BsonRepresentation(BsonType.String)]
        public PlanStatus Status { get; set; } = PlanStatus.Draft;

        public List<PlanDay> Days { get; set; } = new();

        [BsonRepresentation(BsonType.String)]
        public ProgressionScheme Progression { get; set; } = ProgressionScheme.DoubleProgression;

        [BsonRepresentation(BsonType.String)]
        public GenerationSource Source { get; set; } = GenerationSource.Manual;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? AcceptedAt { get; set; }
    }

    public class PlanDay
    {
        public int DayIndex { get; set; }

        [MaxLength(200)]
        public string Label { get; set; } = string.Empty;

        public List<PlannedExercise> Exercises { get; set; } = new();
    }

    public class PlannedExercise
    {
        [BsonRepresentation(BsonType.ObjectId)]
        public string? ExerciseId { get; set; }

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        // Denormalized from Exercise.MuscleGroup so a plan is self-contained.
        [MaxLength(100)]
        public string MuscleGroup { get; set; } = string.Empty;

        [Range(1, 100)]
        public int Sets { get; set; }

        [Range(0, 100)]
        public int RepMin { get; set; }

        [Range(0, 100)]
        public int RepMax { get; set; }

        [Range(0, 10)]
        public int TargetRpe { get; set; }

        [Range(0, 600)]
        public int RestSeconds { get; set; }

        public int Order { get; set; }

        [MaxLength(1000)]
        public string? Notes { get; set; }
    }
}
