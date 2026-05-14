using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    /// <summary>
    /// Append-only record of every model training run. Read by the drift-check
    /// path to compare current holdout RMSE against the last trained baseline,
    /// and by the trainer-facing dashboard to show "last trained at / quality".
    /// </summary>
    public class MetricsLog
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>"manual" | "scheduled" | "drift" | "bootstrap"</summary>
        public string Trigger { get; set; } = "manual";

        public int RowCount          { get; set; }
        public int RealRowCount      { get; set; }
        public int SyntheticRowCount { get; set; }

        public double Rmse       { get; set; }
        public double MeanAbsErr { get; set; }
        public double RSquared   { get; set; }

        /// <summary>Wall-clock time the train + save took, for capacity planning.</summary>
        public double DurationSeconds { get; set; }
    }
}
