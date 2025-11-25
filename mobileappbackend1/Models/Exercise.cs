using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;


namespace mobileappbackend1.Models
{
    //public enum MuscleGroup { Chest, UpperBack,LowerBack, Quads, Hammstrings, Bicep, Tricep, Shoulder, Core, Glutes, Forearm, Calf,  FullBody }
    public class Exercise
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; } = string.Empty;
        public string MuscleGroup { get; set; } = string.Empty;
        public string Equipment { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string? CreatedByTrainerId { get; set; }
    }
}
