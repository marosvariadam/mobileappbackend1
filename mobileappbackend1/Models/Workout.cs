using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum WorkoutStatus { Planned, InProgress, Completed }

    public class Workout
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [MaxLength(300)]
        public string Title { get; set; } = string.Empty;

        // Session-level notes from the trainer, visible to athlete before they start
        [MaxLength(2000)]
        public string? TrainerNotes { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [Required]
        public DateTime ScheduledDate { get; set; }

        [BsonRepresentation(BsonType.String)]
        public WorkoutStatus Status { get; set; } = WorkoutStatus.Planned;

        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Session-level feedback sent back to the trainer when the athlete submits
        [MaxLength(2000)]
        public string? AthleteFeedback { get; set; }

        public List<WorkoutExercise> Exercises { get; set; } = new();
    }

    public class WorkoutExercise
    {
        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string ExerciseId { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        // ── Prescribed by trainer ──────────────────────────────────────
        [Range(1, 100)]
        public int Sets { get; set; }

        [Range(1, 10000)]
        public int TargetRepetitions { get; set; }

        [Range(0, 10000)]
        public double TargetWeightKg { get; set; }

        [MaxLength(1000)]
        public string? TrainerNotes { get; set; }

        // ── Logged by athlete ──────────────────────────────────────────
        public int? ActualSets { get; set; }
        public int? ActualRepetitions { get; set; }
        public double? ActualWeightKg { get; set; }
        public bool IsCompleted { get; set; } = false;

        [MaxLength(1000)]
        public string? AthleteNotes { get; set; }
    }
}
