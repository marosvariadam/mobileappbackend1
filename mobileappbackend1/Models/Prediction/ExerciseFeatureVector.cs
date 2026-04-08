using Microsoft.ML.Data;

namespace mobileappbackend1.Models.Prediction
{
    /// <summary>
    /// One training row = one ISO week of training for one athlete for one exercise.
    /// All numeric features are float because ML.NET's LightGBM pipeline requires float columns.
    /// </summary>
    public class ExerciseFeatureVector
    {
        // ── Per-exercise this week ─────────────────────────────────────────────

        /// <summary>Best Epley 1RM estimate this week: max(weight × (1 + reps/30))</summary>
        public float CurrentOneRmKg { get; set; }

        /// <summary>Σ(actualSets × actualReps × actualWeightKg) for the target exercise this week.</summary>
        public float WeeklyVolumeKg { get; set; }

        /// <summary>Number of sessions containing the target exercise this week.</summary>
        public float SessionCount { get; set; }

        // ── Context features ──────────────────────────────────────────────────

        /// <summary>
        /// Weighted muscle-overlap score (0–1) for all work done this week relative to the target lift.
        /// 1.0 = all training this week was directly relevant; 0.0 = none of it was.
        /// </summary>
        public float OverlapScore { get; set; }

        /// <summary>Weeks elapsed since the athlete's first ever logged workout (training age proxy).</summary>
        public float TrainingAgeWeeks { get; set; }

        /// <summary>Athlete's bodyweight in kg at prediction time. Defaults to 75 if not set.</summary>
        public float BodyweightKg { get; set; }

        /// <summary>Average Workout.Difficulty mapped to 1–4 scale across sessions this week.</summary>
        public float AvgDifficultyScore { get; set; }

        // ── Lag features ──────────────────────────────────────────────────────

        /// <summary>Epley 1RM from the previous week (lag-1). Zero if no prior week.</summary>
        public float PrevWeekOneRmKg { get; set; }

        /// <summary>Total volume from the previous week (lag-1). Zero if no prior week.</summary>
        public float PrevWeekVolumeKg { get; set; }

        /// <summary>
        /// Linear slope of the last 4 weeks' CurrentOneRmKg values.
        /// Positive = recent upward trend; negative = regression.
        /// Zero if fewer than 2 data points.
        /// </summary>
        public float FourWeekTrendSlope { get; set; }

        // ── Label (training target) ───────────────────────────────────────────

        /// <summary>
        /// nextWeek.CurrentOneRmKg − thisWeek.CurrentOneRmKg.
        /// This is what LightGBM learns to predict.
        /// Not set on the most-recent row (used as prediction input, not training row).
        /// </summary>
        [ColumnName("Label")]
        public float NextWeekOneRmDelta { get; set; }

        // ── Metadata (not used as ML features) ───────────────────────────────

        [NoColumn]
        public DateTime WeekStart { get; set; }
    }

    /// <summary>Output schema expected from the LightGBM regression pipeline.</summary>
    public class PredictionOutput
    {
        [ColumnName("Score")]
        public float PredictedDelta { get; set; }
    }
}
