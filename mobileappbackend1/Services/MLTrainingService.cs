using System.Diagnostics;
using Microsoft.Extensions.Options;
using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Settings;

namespace mobileappbackend1.Services
{
    /// <summary>
    /// Top-level training orchestrator. Pulls real labeled rows from history,
    /// generates synthetic rows, blends the two with a real-row-count-driven
    /// weight, hands the merged set to <see cref="ProgressTrainer"/>, persists
    /// metrics, and pokes the live <see cref="PredictionEngineService"/> to
    /// reload. Idempotent — safe to call from a hosted service or a manual
    /// endpoint without coordination.
    /// </summary>
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

        /// <summary>
        /// Run a full retrain end-to-end. <paramref name="trigger"/> is recorded
        /// on the resulting <see cref="MetricsLog"/> ("manual" / "scheduled" /
        /// "drift" / "bootstrap").
        /// </summary>
        public async Task<MetricsLog> TrainAndSaveAsync(
            string trigger = "manual",
            CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();

            // ── Real rows ─────────────────────────────────────────────────────
            // Last 2 years of history. Older data isn't predictive of current
            // training-age rows for the same athlete and bloats the dataset.
            var to = DateTime.UtcNow;
            var from = to.AddYears(-2);
            var realRows = await _featureService.BuildAllLabeledRowsAsync(from, to);
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Collected {Real} real labeled rows.", realRows.Count);

            // ── Synthetic rows ────────────────────────────────────────────────
            var syntheticRows = _generator.Generate();
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Generated {Syn} synthetic labeled rows.", syntheticRows.Count);

            // ── Blend ─────────────────────────────────────────────────────────
            // Synthetic shrinks as real grows. Below 1k real → keep all synthetic
            // (bootstrap mode). Past 200k real → drop synthetic entirely.
            var syntheticToInclude = SubsampleSynthetic(realRows.Count, syntheticRows.Count);
            var blended = new List<LabeledTrainingRow>(realRows.Count + syntheticToInclude);
            blended.AddRange(realRows);
            if (syntheticToInclude == syntheticRows.Count)
                blended.AddRange(syntheticRows);
            else if (syntheticToInclude > 0)
            {
                // Deterministic subsample so two consecutive trains see the same
                // synthetic slice unless real data changed.
                var rng = new Random(1);
                var indices = Enumerable.Range(0, syntheticRows.Count)
                                         .OrderBy(_ => rng.Next())
                                         .Take(syntheticToInclude);
                foreach (var idx in indices) blended.Add(syntheticRows[idx]);
            }

            _logger.LogInformation(
                "Blended dataset: {Total} rows ({Real} real + {Syn} synthetic).",
                blended.Count, realRows.Count, syntheticToInclude);

            // ── Train + save ──────────────────────────────────────────────────
            var modelPath = ResolveModelPath();
            ct.ThrowIfCancellationRequested();

            var result = _trainer.TrainAndSave(blended, modelPath);
            stopwatch.Stop();

            _logger.LogInformation(
                "Training complete. RMSE={Rmse:F3} kg, MAE={Mae:F3} kg, R²={R2:F3}, took {Sec:F1}s.",
                result.Rmse, result.MeanAbsErr, result.RSquared, stopwatch.Elapsed.TotalSeconds);

            // ── Reload live model ─────────────────────────────────────────────
            // FileSystemWatcher would catch this anyway, but reload synchronously
            // so the next prediction call is guaranteed to see the new model.
            _predictionService.Reload();

            // ── Persist metrics ───────────────────────────────────────────────
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

        // Real-row-count → synthetic count to include. Matches the spec:
        //   < 1,000 real         → keep all synthetic
        //   1,000 – 200,000 real → linearly shrink, floor at 10% of synthetic
        //   > 200,000 real       → drop synthetic
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
