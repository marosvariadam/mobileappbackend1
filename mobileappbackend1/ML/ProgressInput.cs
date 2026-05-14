using Microsoft.ML.Data;

namespace mobileappbackend1.ML
{
    /// <summary>
    /// Flat row handed to the ML.NET pipeline — all numeric fields are <c>float</c>
    /// (LightGBM's native type) and all categoricals are <c>string</c> that the
    /// pipeline one-hot encodes. Nullable features from <see cref="WeeklyFeatureVector"/>
    /// are filled with neutral defaults by <see cref="ToInput"/>.
    /// </summary>
    public class ProgressInput
    {
        public string ExerciseName { get; set; } = string.Empty;
        public string MuscleGroup  { get; set; } = string.Empty;
        public string Focus        { get; set; } = "Full";

        public float VolumeKg         { get; set; }
        public float Est1Rm           { get; set; }
        public float AvgRpe           { get; set; }
        public float SetCount         { get; set; }
        public float TotalReps        { get; set; }
        public float OverlapScore     { get; set; }
        public float BodyweightKg     { get; set; }
        public float TrainingAgeWeeks { get; set; }
        public float IsBeginner       { get; set; }
        public float PrevWeekEst1Rm   { get; set; }
        public float PrevWeekVolumeKg { get; set; }

        public float Label { get; set; }  // next-week Est1Rm delta (kg)

        /// <summary>Build a training row from a labeled example.</summary>
        public static ProgressInput From(LabeledTrainingRow row)
        {
            var input = FromFeatures(row.Features);
            input.Label = (float)row.NextWeekEst1RmDelta;
            return input;
        }

        /// <summary>
        /// Build an inference-time row (no label). Missing RPE falls back to 7
        /// (moderate effort); missing lag features fall back to the current-week
        /// value so the model sees "no change from last week" rather than zero.
        /// </summary>
        public static ProgressInput FromFeatures(WeeklyFeatureVector f) => new()
        {
            ExerciseName     = f.ExerciseName ?? string.Empty,
            MuscleGroup      = f.MuscleGroup  ?? string.Empty,
            Focus            = string.IsNullOrWhiteSpace(f.Focus) ? "Full" : f.Focus,
            VolumeKg         = (float)f.VolumeKg,
            Est1Rm           = (float)f.Est1Rm,
            AvgRpe           = (float)(f.AvgRpe ?? 7.0),
            SetCount         = f.SetCount,
            TotalReps        = f.TotalReps,
            OverlapScore     = (float)f.OverlapScore,
            BodyweightKg     = (float)f.BodyweightKg,
            TrainingAgeWeeks = f.TrainingAgeWeeks,
            IsBeginner       = f.IsBeginner ? 1f : 0f,
            PrevWeekEst1Rm   = (float)(f.PrevWeekEst1Rm   ?? f.Est1Rm),
            PrevWeekVolumeKg = (float)(f.PrevWeekVolumeKg ?? f.VolumeKg),
        };
    }

    /// <summary>Regressor output: predicted next-week <c>Est1Rm</c> delta (kg).</summary>
    public class ProgressPrediction
    {
        [ColumnName("Score")]
        public float PredictedDelta { get; set; }
    }
}
