using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;


namespace mobileappbackend1.Models
{
    public class Workout
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }
        public string Title { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; }

        public DateTime ScheduledDate { get; set; }

        public bool IsCompleted { get; set; } = false;

        public List<WorkoutExercise> Exercises { get; set; } = new List<WorkoutExercise>();
    }
    public class WorkoutExercise
    {
        [BsonRepresentation(BsonType.ObjectId)]
        public string ExerciseId { get; set; }

        public string Name { get; set; }
        public int Sets { get; set; }
        public int TargetRepetitions { get; set; }
        public double TargetWeightKg { get; set; } // kg

        public string? AthleteNotes { get; set; }

        public string? TrainerNotes { get; set; }
    }
}



