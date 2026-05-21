namespace mobileappbackend1.Settings
{
    /// <summary>Configuration for the progress-prediction pipeline.</summary>
    public class MLSettings
    {
        /// <summary>Path to the trained model .zip; watched for changes so retraining hot-reloads.</summary>
        public string ModelPath { get; set; } = "App_Data/ml/progress-model.zip";

        /// <summary>Logical name the prediction pool registers under.</summary>
        public string ModelName { get; set; } = "progress";

        /// <summary>Weekly cadence for the scheduled retrain (hosted service).</summary>
        public int RetrainIntervalHours { get; set; } = 168;

        /// <summary>Skip a scheduled retrain if fewer than this many new rows arrived.</summary>
        public int MinNewRowsToRetrain { get; set; } = 50;

        /// <summary>Fractional RMSE drift that triggers an off-cycle retrain.</summary>
        public double DriftRmseThreshold { get; set; } = 0.15;

        /// <summary>How many synthetic athletes to generate when bootstrapping.</summary>
        public int SyntheticAthleteCount { get; set; } = 2000;
    }
}
