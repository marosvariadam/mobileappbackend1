using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    // Audit / evaluation record for a single plan-generation attempt.
    // Written by LlmPlanService and the repair loop; used by the thesis
    // evaluation harness to compare generation modes.
    public class PlanGenerationLog
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string? PlanId { get; set; }

        [BsonRepresentation(BsonType.String)]
        public GenerationSource Mode { get; set; } = GenerationSource.Manual;

        // Snapshot of the profile used as input (anonymized).
        public AthleteProfile? ProfileSnapshot { get; set; }

        // Each entry is one iteration of the LLM + validator loop.
        public List<GenerationAttempt> Attempts { get; set; } = new();

        public int RepairAttemptCount { get; set; }

        public TrainingPlan? FinalPlanSnapshot { get; set; }

        // Set when the trainer accepts the plan; captures any edits they made.
        public TrainingPlan? TrainerFinalSnapshot { get; set; }

        public long LatencyMs { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }

        public double? PredictedProgressScore { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class GenerationAttempt
    {
        public int AttemptIndex { get; set; }
        public string? RawOutput { get; set; }
        public List<ViolationRecord> Violations { get; set; } = new();
    }

    // Flat snapshot of a validator Violation for serialization in the log.
    public class ViolationRecord
    {
        public string Code { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int? DayIndex { get; set; }
        public string? ExerciseId { get; set; }
    }
}
