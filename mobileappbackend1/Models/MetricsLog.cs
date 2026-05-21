using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    /// <summary>Append-only record of one ML training run.</summary>
    public class MetricsLog
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>One of: "manual", "scheduled", "drift", "bootstrap".</summary>
        public string Trigger { get; set; } = "manual";

        public int RowCount          { get; set; }
        public int RealRowCount      { get; set; }
        public int SyntheticRowCount { get; set; }

        public double Rmse       { get; set; }
        public double MeanAbsErr { get; set; }
        public double RSquared   { get; set; }

        /// <summary>Wall-clock time the train + save took.</summary>
        public double DurationSeconds { get; set; }
    }
}
