using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public class Workout
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [Required]
        [MaxLength(300)]
        public string Title { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [Required]
        public DateTime ScheduledDate { get; set; }

        public bool IsCompleted { get; set; } = false;

        public List<WorkoutExercise> Exercises { get; set; } = new List<WorkoutExercise>();
    }

    public class WorkoutExercise
    {
        [Required]
        [BsonRepresentation(BsonType.ObjectId)]
        public string ExerciseId { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Range(1, 100)]
        public int Sets { get; set; }

        [Range(1, 10000)]
        public int TargetRepetitions { get; set; }

        [Range(0, 10000)]
        public double TargetWeightKg { get; set; }

        [MaxLength(1000)]
        public string? AthleteNotes { get; set; }

        [MaxLength(1000)]
        public string? TrainerNotes { get; set; }
    }
}
