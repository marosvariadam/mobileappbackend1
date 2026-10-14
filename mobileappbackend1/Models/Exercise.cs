using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum MovementPattern
    {
        HorizontalPush,
        VerticalPush,
        HorizontalPull,
        VerticalPull,
        Squat,
        Hinge,
        Lunge,
        Carry,
        Isolation,
        Core
    }

    public enum EquipmentType
    {
        Barbell,
        Dumbbell,
        Machine,
        Cable,
        Bodyweight,
        Kettlebell,
        Band
    }

    public enum ExperienceLevel
    {
        Beginner,
        Intermediate,
        Advanced
    }

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

        // Primary muscle group. Must stay stable: ML.NET pipeline keys off this.
        // One of: Chest, Back, Shoulders, Legs, Arms, Core.
        [MaxLength(100)]
        public string MuscleGroup { get; set; } = string.Empty;

        // Lightly loaded synergists (ratio 0.5 in volume accounting).
        public List<string> SecondaryMuscleGroups { get; set; } = new();

        [BsonRepresentation(BsonType.String)]
        public MovementPattern MovementPattern { get; set; } = MovementPattern.Isolation;

        public bool IsCompound { get; set; }

        [BsonRepresentation(BsonType.String)]
        public ExperienceLevel MinLevel { get; set; } = ExperienceLevel.Beginner;

        // Equipment the exercise can be performed with. BSON field name differs
        // from the legacy free-text "Equipment" string so legacy docs don't
        // break deserialization during the migration window.
        [BsonElement("EquipmentTypes")]
        public List<EquipmentType> Equipment { get; set; } = new();

        // Joints / regions loaded. Used by PlanValidator to honor AvoidStressTags.
        // Common values: knee, lower_back, shoulder, wrist, elbow, hip, ankle, neck.
        public List<string> StressTags { get; set; } = new();

        [BsonRepresentation(BsonType.ObjectId)]
        public string? CreatedByTrainerId { get; set; }
    }
}
