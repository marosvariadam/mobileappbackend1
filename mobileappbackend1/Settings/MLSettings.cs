namespace mobileappbackend1.Settings
{
    /// <summary>
    /// Knobs for the progress-prediction pipeline. Path resolution for
    /// <see cref="ModelPath"/> is done in <c>Program.cs</c> (relative to
    /// <see cref="Microsoft.Extensions.Hosting.IHostEnvironment.ContentRootPath"/>).
    /// </summary>
    public class MLSettings
    {
        /// <summary>
        /// Where the trained LightGBM model .zip lives on disk. The prediction
        /// pool watches this path and reloads on change so retraining picks up
        /// without a restart.
        /// </summary>
        public string ModelPath { get; set; } = "App_Data/ml/progress-model.zip";

        /// <summary>Logical name the prediction pool registers under.</summary>
        public string ModelName { get; set; } = "progress";

        /// <summary>Weekly cadence for the scheduled retrain (hosted service).</summary>
        public int RetrainIntervalHours { get; set; } = 168;

        /// <summary>Skip a scheduled retrain if fewer than this many new rows arrived.</summary>
        public int MinNewRowsToRetrain { get; set; } = 50;

        /// <summary>Fractional RMSE drift that triggers an off-cycle retrain.</summary>
        public double DriftRmseThreshold { get; set; } = 0.15;

        /// <summary>
        /// How many synthetic athletes to generate when bootstrapping. Dropped to
        /// zero once real rows dominate — see the weighting rule in Phase 6.
        /// </summary>
        public int SyntheticAthleteCount { get; set; } = 2000;
    }
}
