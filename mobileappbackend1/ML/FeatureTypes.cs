namespace mobileappbackend1.ML
{
    /// <summary>
    /// One week of aggregated history for a single athlete / exercise pair.
    /// Produced by <c>FeatureEngineeringService</c>, consumed both by the
    /// prediction endpoint (chart series) and by the ML training pipeline
    /// (after pairing with a next-week label).
    /// </summary>
    public class WeeklyFeatureVector
    {
        public DateTime WeekStart { get; set; }                // Monday 00:00 UTC
        public string AthleteId { get; set; } = string.Empty;
        public string ExerciseName { get; set; } = string.Empty;
        public string? MuscleGroup { get; set; }
        public string Focus { get; set; } = "Full";            // "Full" when no block covers the week

        public double VolumeKg { get; set; }                   // Σ actualSets × actualReps × actualWeightKg
        public double Est1Rm { get; set; }                     // max Epley across logged sets that week
        public double? AvgRpe { get; set; }
        public int SetCount { get; set; }
        public int TotalReps { get; set; }

        public double OverlapScore { get; set; }               // MuscleOverlap(focus, muscleGroup)
        public double BodyweightKg { get; set; }
        public int TrainingAgeWeeks { get; set; }
        public bool IsBeginner { get; set; }

        // Lagged features — null for the first observed week.
        public double? PrevWeekEst1Rm { get; set; }
        public double? PrevWeekVolumeKg { get; set; }
    }

    /// <summary>
    /// A labeled row used for model training: this week's features paired with
    /// next week's 1RM delta (kg). Only generated when both weeks have data.
    /// </summary>
    public class LabeledTrainingRow
    {
        public WeeklyFeatureVector Features { get; set; } = new();
        public double NextWeekEst1RmDelta { get; set; }
    }
}
