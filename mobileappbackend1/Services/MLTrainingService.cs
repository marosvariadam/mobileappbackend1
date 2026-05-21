using System.Diagnostics;
using Microsoft.Extensions.Options;
using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Settings;

namespace mobileappbackend1.Services
{
    /// <summary>Runs a training pass: gather real + synthetic rows, train, save, reload, log metrics.</summary>
    public class MLTrainingService
    {
        private readonly FeatureEngineeringService _featureService;
        private readonly SyntheticDataGenerator _generator;
        private readonly ProgressTrainer _trainer;
        private readonly PredictionEngineService _predictionService;
        private readonly MetricsLogService _metricsService;
        private readonly MLSettings _settings;
        private readonly IHostEnvironment _env;
        private readonly ILogger<MLTrainingService> _logger;

        public MLTrainingService(
            FeatureEngineeringService featureService,
            SyntheticDataGenerator generator,
            ProgressTrainer trainer,
            PredictionEngineService predictionService,
            MetricsLogService metricsService,
            IOptions<MLSettings> settings,
            IHostEnvironment env,
            ILogger<MLTrainingService> logger)
        {
            _featureService = featureService;
            _generator = generator;
            _trainer = trainer;
            _predictionService = predictionService;
            _metricsService = metricsService;
            _settings = settings.Value;
            _env = env;
            _logger = logger;
        }

        /// <summary>Run a full retrain end-to-end and append a MetricsLog row.</summary>
        public async Task<MetricsLog> TrainAndSaveAsync(
            string trigger = "manual",
            CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();

            // Last 2 years of history.
            var to = DateTime.UtcNow;
            var from = to.AddYears(-2);
            var realRows = await _featureService.BuildAllLabeledRowsAsync(from, to);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Collected {Real} real labeled rows.", realRows.Count);

            var syntheticRows = _generator.Generate();
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Generated {Syn} synthetic labeled rows.", syntheticRows.Count);

            // Synthetic shrinks as real grows; <1k real keeps all, >200k drops all.
            var syntheticToInclude = SubsampleSynthetic(realRows.Count, syntheticRows.Count);
            var blended = new List<LabeledTrainingRow>(realRows.Count + syntheticToInclude);
            blended.AddRange(realRows);
            if (syntheticToInclude == syntheticRows.Count)
                blended.AddRange(syntheticRows);
            else if (syntheticToInclude > 0)
            {
                // Deterministic subsample using a fixed seed.
                var rng = new Random(1);
                var indices = Enumerable.Range(0, syntheticRows.Count)
                                         .OrderBy(_ => rng.Next())
                                         .Take(syntheticToInclude);
                foreach (var idx in indices) blended.Add(syntheticRows[idx]);
            }

            _logger.LogInformation(
                "Blended dataset: {Total} rows ({Real} real + {Syn} synthetic).",
                blended.Count, realRows.Count, syntheticToInclude);

            var modelPath = ResolveModelPath();
            ct.ThrowIfCancellationRequested();

            var result = _trainer.TrainAndSave(blended, modelPath);
            stopwatch.Stop();

            _logger.LogInformation(
                "Training complete. RMSE={Rmse:F3} kg, MAE={Mae:F3} kg, R^2={R2:F3}, took {Sec:F1}s.",
                result.Rmse, result.MeanAbsErr, result.RSquared, stopwatch.Elapsed.TotalSeconds);

            // Reload synchronously so the next prediction sees the new model.
            _predictionService.Reload();

            var log = new MetricsLog
            {
                CreatedAt         = DateTime.UtcNow,
                Trigger           = trigger,
                RowCount          = result.RowCount,
                RealRowCount      = realRows.Count,
                SyntheticRowCount = syntheticToInclude,
                Rmse              = result.Rmse,
                MeanAbsErr        = result.MeanAbsErr,
                RSquared          = result.RSquared,
                DurationSeconds   = stopwatch.Elapsed.TotalSeconds,
            };
            await _metricsService.AppendAsync(log);

            return log;
        }

        // Real-row-count to synthetic count to include.
        private static int SubsampleSynthetic(int realCount, int syntheticTotal)
        {
            if (realCount < 1_000)   return syntheticTotal;
            if (realCount > 200_000) return 0;

            var weight = Math.Max(0.1, 2_000.0 / realCount);
            return (int)Math.Round(syntheticTotal * weight);
        }

        private string ResolveModelPath()
        {
            return Path.IsPathRooted(_settings.ModelPath)
                ? _settings.ModelPath
                : Path.Combine(_env.ContentRootPath, _settings.ModelPath);
        }
    }
}
