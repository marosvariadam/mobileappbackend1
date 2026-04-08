namespace mobileappbackend1.Models.Prediction
{
    public class ProgressPredictionResponse
    {
        public string ExerciseName { get; set; } = string.Empty;
        public string AthleteId   { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>"personalized" | "hybrid" | "cold-start"</summary>
        public string DataSource { get; set; } = string.Empty;

        /// <summary>Number of real logged weeks used for the model.</summary>
        public int WeeksOfRealData { get; set; }

        /// <summary>
        /// Historical week-by-week actuals. Flutter uses this for the solid line on the chart.
        /// </summary>
        public List<ActualWeekPoint> ActualHistory { get; set; } = new();

        /// <summary>
        /// Model-projected weeks. Flutter uses this for the dashed/projected line.
        /// </summary>
        public List<ProjectedWeekPoint> ProjectedProgress { get; set; } = new();

        /// <summary>
        /// Fraction of total weekly volume per muscle-group category over the last 4 weeks.
        /// Flutter can render this as a small pie/bar for context.
        /// e.g. {"legs": 0.60, "push": 0.25, "pull": 0.15}
        /// </summary>
        public Dictionary<string, float> MuscleFocusDistribution { get; set; } = new();
    }

    public class ActualWeekPoint
    {
        /// <summary>Monday of that ISO week (UTC).</summary>
        public DateTime WeekStart    { get; set; }
        public float EstimatedOneRmKg { get; set; }
        public float TotalVolumeKg   { get; set; }
        public int   SessionCount    { get; set; }
    }

    public class ProjectedWeekPoint
    {
        public DateTime WeekStart        { get; set; }
        public float PredictedOneRmKg    { get; set; }
        /// <summary>Lower bound of 80 % confidence interval.</summary>
        public float ConfidenceLow       { get; set; }
        /// <summary>Upper bound of 80 % confidence interval.</summary>
        public float ConfidenceHigh      { get; set; }
    }

    /// <summary>Lightweight response for listing exercises that have enough history to predict.</summary>
    public class TrainableExercise
    {
        public string ExerciseName  { get; set; } = string.Empty;
        public int    WeeksOfData   { get; set; }
        public float  LatestOneRmKg { get; set; }
    }
}
