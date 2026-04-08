using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;
using mobileappbackend1.Models.Prediction;

namespace mobileappbackend1.Services.ML
{
    /// <summary>
    /// Singleton service that owns the ML context, model cache, and model store.
    ///
    /// Lifecycle:
    ///   – Models are trained on-demand the first time a prediction is requested.
    ///   – Trained models are persisted to <see cref="ModelStorePath"/> as .zip files.
    ///   – <see cref="ModelRetrainBackgroundService"/> calls <see cref="TrainAndSaveAsync"/>
    ///     weekly to keep models current.
    ///
    /// Thread safety:
    ///   – <see cref="MLContext"/> is thread-safe after construction.
    ///   – The model cache uses <see cref="ConcurrentDictionary"/> + SemaphoreSlim per key
    ///     to prevent redundant concurrent training of the same model.
    /// </summary>
    public class ProgressPredictionService
    {
        public static readonly string ModelStorePath =
            Path.Combine(Directory.GetCurrentDirectory(), "ModelStore");

        // Minimum real weeks before we switch from cold-start to personalized model
        private const int MinWeeksForPersonalised = 8;
        private const int MinWeeksForHybrid       = 4;

        // How many weeks of synthetic data to blend in for a hybrid model
        private const int SyntheticWeeksForHybrid = 52;

        private readonly MLContext             _mlContext  = new(seed: 42);
        private readonly SyntheticDataService  _synthetic;
        private readonly IServiceScopeFactory  _scopeFactory;
        private readonly ILogger<ProgressPredictionService> _logger;

        // key = "{athleteId}_{normExerciseName}"
        private readonly ConcurrentDictionary<string, CachedModel> _cache = new();
        // Per-key semaphore prevents two threads from training the same model simultaneously
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks  = new();

        public ProgressPredictionService(
            SyntheticDataService                       synthetic,
            IServiceScopeFactory                       scopeFactory,
            ILogger<ProgressPredictionService>         logger)
        {
            _synthetic    = synthetic;
            _scopeFactory = scopeFactory;
            _logger       = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a full prediction response for the Flutter chart.
        /// Trains a new model if none is cached or if the cache is stale (> 7 days).
        /// </summary>
        public async Task<ProgressPredictionResponse> GetPredictionAsync(
            string athleteId,
            string exerciseName,
            int    weeksAhead = 12)
        {
            weeksAhead = Math.Clamp(weeksAhead, 1, 52);

            using var scope = _scopeFactory.CreateScope();
            var featureSvc  = scope.ServiceProvider.GetRequiredService<FeatureEngineeringService>();

            var vectors = await featureSvc.GetWeeklyVectorsAsync(athleteId, exerciseName);
            var muscleFocus = await featureSvc.GetMuscleFocusDistributionAsync(athleteId);

            var cacheKey = BuildKey(athleteId, exerciseName);
            var cached   = await GetOrTrainModelAsync(cacheKey, athleteId, exerciseName, vectors);

            var actualHistory = vectors
                .Select(v => new ActualWeekPoint
                {
                    WeekStart         = v.WeekStart,
                    EstimatedOneRmKg  = v.CurrentOneRmKg,
                    TotalVolumeKg     = v.WeeklyVolumeKg,
                    SessionCount      = (int)v.SessionCount,
                })
                .ToList();

            // Seed the roll-forward from the last known real vector
            var seed = vectors.Count > 0 ? vectors.Last() : BuildColdStartSeed(athleteId, exerciseName);

            var projected = RollForward(cached, seed, weeksAhead);

            return new ProgressPredictionResponse
            {
                ExerciseName           = exerciseName,
                AthleteId              = athleteId,
                GeneratedAt            = DateTime.UtcNow,
                DataSource             = cached.DataSource,
                WeeksOfRealData        = vectors.Count,
                ActualHistory          = actualHistory,
                ProjectedProgress      = projected,
                MuscleFocusDistribution = muscleFocus,
            };
        }

        /// <summary>
        /// Trains (or retrains) and persists the model for a given athlete/exercise.
        /// Called by <see cref="BackgroundServices.ModelRetrainBackgroundService"/>.
        /// </summary>
        public async Task TrainAndSaveAsync(string athleteId, string exerciseName)
        {
            using var scope = _scopeFactory.CreateScope();
            var featureSvc  = scope.ServiceProvider.GetRequiredService<FeatureEngineeringService>();
            var vectors     = await featureSvc.GetWeeklyVectorsAsync(athleteId, exerciseName);

            var cacheKey = BuildKey(athleteId, exerciseName);
            await TrainInternalAsync(cacheKey, athleteId, exerciseName, vectors);
        }

        // ── Model retrieval + lazy training ──────────────────────────────────

        private async Task<CachedModel> GetOrTrainModelAsync(
            string                       cacheKey,
            string                       athleteId,
            string                       exerciseName,
            List<ExerciseFeatureVector>   vectors)
        {
            if (_cache.TryGetValue(cacheKey, out var hit) && !hit.IsStale)
                return hit;

            return await TrainInternalAsync(cacheKey, athleteId, exerciseName, vectors);
        }

        private async Task<CachedModel> TrainInternalAsync(
            string                       cacheKey,
            string                       athleteId,
            string                       exerciseName,
            List<ExerciseFeatureVector>   realVectors)
        {
            var sem = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Re-check cache after acquiring the lock (another thread may have just trained)
                if (_cache.TryGetValue(cacheKey, out var refreshed) && !refreshed.IsStale)
                    return refreshed;

                _logger.LogInformation("Training ML model for {Key}", cacheKey);

                var (trainingRows, dataSource) = PrepareTrainingData(
                    athleteId, exerciseName, realVectors);

                // Need at least 6 labelled rows (last row has no label, so count - 1)
                var labelledRows = trainingRows.Where(r => r.NextWeekOneRmDelta != 0f).ToList();
                if (labelledRows.Count < 6)
                {
                    _logger.LogWarning("Not enough data to train {Key}, using global fallback.", cacheKey);
                    return GetOrLoadGlobalFallback(exerciseName, dataSource);
                }

                var trainingData = _mlContext.Data.LoadFromEnumerable(labelledRows);

                var pipeline = BuildPipeline();
                var model    = pipeline.Fit(trainingData);

                // Compute RMSE via cross-validation (min 3 folds, use 2 if small dataset)
                var folds   = labelledRows.Count >= 15 ? 3 : 2;
                double rmse = 0;
                try
                {
                    var cvResults = _mlContext.Regression.CrossValidate(
                        trainingData, pipeline, numberOfFolds: folds);
                    rmse = cvResults.Average(r => r.Metrics.RootMeanSquaredError);
                }
                catch
                {
                    rmse = 2.5; // fallback if CV fails (tiny dataset)
                }

                var cached = new CachedModel
                {
                    Model      = model,
                    Schema     = trainingData.Schema,
                    RmseKg     = (float)rmse,
                    DataSource = dataSource,
                    TrainedAt  = DateTime.UtcNow,
                };

                _cache[cacheKey] = cached;

                // Persist to disk
                SaveModel(cacheKey, cached, trainingData.Schema);

                return cached;
            }
            finally
            {
                sem.Release();
            }
        }

        // ── Training data preparation ─────────────────────────────────────────

        private (List<ExerciseFeatureVector> rows, string dataSource) PrepareTrainingData(
            string athleteId,
            string exerciseName,
            List<ExerciseFeatureVector> realVectors)
        {
            int realCount = realVectors.Count;

            if (realCount >= MinWeeksForPersonalised)
            {
                return (realVectors, "personalized");
            }

            using var scope = _scopeFactory.CreateScope();
            var userSvc     = scope.ServiceProvider.GetRequiredService<UserService>();

            // Synchronously fetch bodyweight (acceptable here since we're under a semaphore)
            var user       = userSvc.GetByIdAsync(athleteId).GetAwaiter().GetResult();
            var bodyweight = (float)(user?.WeightKg ?? 75f);

            var trainingAge = realCount > 0
                ? realVectors.Last().TrainingAgeWeeks
                : 0f;

            var startingOneRm = realCount > 0
                ? (float?)realVectors.Last().CurrentOneRmKg
                : null;

            var synthetic = _synthetic.GenerateCurve(
                exerciseName, bodyweight, startingOneRm,
                trainingAge, SyntheticWeeksForHybrid);

            if (realCount >= MinWeeksForHybrid)
            {
                // Hybrid: triplicate real data so it outweighs synthetic
                var hybrid = new List<ExerciseFeatureVector>(synthetic);
                for (int i = 0; i < 3; i++) hybrid.AddRange(realVectors);
                return (hybrid, "hybrid");
            }

            return (synthetic, "cold-start");
        }

        // ── ML.NET pipeline ───────────────────────────────────────────────────

        private static readonly string[] FeatureColumns =
        {
            nameof(ExerciseFeatureVector.CurrentOneRmKg),
            nameof(ExerciseFeatureVector.WeeklyVolumeKg),
            nameof(ExerciseFeatureVector.SessionCount),
            nameof(ExerciseFeatureVector.OverlapScore),
            nameof(ExerciseFeatureVector.TrainingAgeWeeks),
            nameof(ExerciseFeatureVector.BodyweightKg),
            nameof(ExerciseFeatureVector.AvgDifficultyScore),
            nameof(ExerciseFeatureVector.PrevWeekOneRmKg),
            nameof(ExerciseFeatureVector.PrevWeekVolumeKg),
            nameof(ExerciseFeatureVector.FourWeekTrendSlope),
        };

        private IEstimator<ITransformer> BuildPipeline()
        {
            var options = new LightGbmRegressionTrainer.Options
            {
                LabelColumnName   = "Label",
                FeatureColumnName = "Features",
                NumberOfLeaves              = 20,
                MinimumExampleCountPerLeaf  = 2,
                NumberOfIterations          = 150,
                LearningRate                = 0.05,
                Verbose                     = false,
            };

            return _mlContext.Transforms
                .Concatenate("Features", FeatureColumns)
                .Append(_mlContext.Transforms.NormalizeMeanVariance("Features"))
                .Append(_mlContext.Regression.Trainers.LightGbm(options));
        }

        // ── Roll-forward prediction ───────────────────────────────────────────

        /// <summary>
        /// Iteratively predicts <paramref name="weeksAhead"/> future weeks by feeding each
        /// prediction back as the input for the next.  Confidence interval widens with the
        /// square root of the number of steps (uncertainty compounds).
        /// </summary>
        private List<ProjectedWeekPoint> RollForward(
            CachedModel           cached,
            ExerciseFeatureVector seed,
            int                   weeksAhead)
        {
            var engine  = _mlContext.Model.CreatePredictionEngine<ExerciseFeatureVector, PredictionOutput>(
                              cached.Model);
            var result  = new List<ProjectedWeekPoint>(weeksAhead);
            var current = CloneVector(seed);
            var rmse    = cached.RmseKg;

            for (int week = 1; week <= weeksAhead; week++)
            {
                var output     = engine.Predict(current);
                var delta      = output.PredictedDelta;

                // Clamp: can't gain more than 5 kg/week or lose more than 10 % at once
                delta = Math.Clamp(delta, -current.CurrentOneRmKg * 0.10f, 5f);

                var predicted  = current.CurrentOneRmKg + delta;

                // CI widens with sqrt(steps) — honest about compounding uncertainty
                var ciHalfWidth = rmse * MathF.Sqrt(week) * 1.28f; // 80 % interval

                result.Add(new ProjectedWeekPoint
                {
                    WeekStart       = current.WeekStart.AddDays(7),
                    PredictedOneRmKg = MathF.Round(predicted, 2),
                    ConfidenceLow    = MathF.Round(MathF.Max(0f, predicted - ciHalfWidth), 2),
                    ConfidenceHigh   = MathF.Round(predicted + ciHalfWidth, 2),
                });

                // Advance the state for next iteration
                current.PrevWeekOneRmKg  = current.CurrentOneRmKg;
                current.PrevWeekVolumeKg = current.WeeklyVolumeKg;
                current.CurrentOneRmKg   = predicted;
                current.TrainingAgeWeeks += 1f;
                current.WeekStart        = current.WeekStart.AddDays(7);

                // Update 4-week trend slope with latest predicted value
                var recentValues = result.TakeLast(4).Select(r => r.PredictedOneRmKg).ToArray();
                current.FourWeekTrendSlope = SyntheticDataService.LinearSlope(recentValues);
            }

            return result;
        }

        // ── Global fallback model ─────────────────────────────────────────────

        private CachedModel GetOrLoadGlobalFallback(string exerciseName, string dataSource)
        {
            var key = $"global_{MuscleOverlapMatrix.NormaliseExerciseName(exerciseName).Replace(' ', '_')}";

            if (_cache.TryGetValue(key, out var existing) && !existing.IsStale)
                return existing;

            // Try loading persisted global model
            var zipPath = Path.Combine(ModelStorePath, $"{key}.zip");
            if (File.Exists(zipPath))
            {
                try
                {
                    using var fs = File.OpenRead(zipPath);
                    var model    = _mlContext.Model.Load(fs, out var schema);
                    var meta     = LoadMeta(zipPath);
                    var cached   = new CachedModel
                    {
                        Model      = model,
                        Schema     = schema,
                        RmseKg     = meta?.RmseKg ?? 3.0f,
                        DataSource = "cold-start",
                        TrainedAt  = meta?.TrainedAt ?? DateTime.UtcNow,
                    };
                    _cache[key] = cached;
                    return cached;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load global fallback model {Key}", key);
                }
            }

            // Train global fallback from synthetic data
            var synthetic = _synthetic.GenerateCurve(exerciseName, 80f, null, 0f, 52);
            var labelled  = synthetic.Where(r => r.NextWeekOneRmDelta != 0f).ToList();
            var data      = _mlContext.Data.LoadFromEnumerable(labelled);
            var pipeline  = BuildPipeline();
            var trainedModel = pipeline.Fit(data);

            var fallback = new CachedModel
            {
                Model      = trainedModel,
                Schema     = data.Schema,
                RmseKg     = 3.5f,
                DataSource = dataSource,
                TrainedAt  = DateTime.UtcNow,
            };
            _cache[key] = fallback;
            SaveModel(key, fallback, data.Schema);
            return fallback;
        }

        // ── Model persistence ─────────────────────────────────────────────────

        private void SaveModel(string key, CachedModel cached, Microsoft.ML.DataViewSchema schema)
        {
            Directory.CreateDirectory(ModelStorePath);
            var zipPath  = Path.Combine(ModelStorePath, $"{key}.zip");
            var metaPath = Path.Combine(ModelStorePath, $"{key}.meta.json");
            try
            {
                using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                _mlContext.Model.Save(cached.Model, schema, fs);

                var meta = new ModelMeta { RmseKg = cached.RmseKg, TrainedAt = cached.TrainedAt };
                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save model {Key} to disk", key);
            }
        }

        private static ModelMeta? LoadMeta(string zipPath)
        {
            var metaPath = Path.ChangeExtension(zipPath, null) + ".meta.json";
            if (!File.Exists(metaPath)) return null;
            try { return JsonSerializer.Deserialize<ModelMeta>(File.ReadAllText(metaPath)); }
            catch { return null; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string BuildKey(string athleteId, string exerciseName) =>
            $"{athleteId}_{MuscleOverlapMatrix.NormaliseExerciseName(exerciseName).Replace(' ', '_')}";

        private static ExerciseFeatureVector CloneVector(ExerciseFeatureVector src) => new()
        {
            WeekStart          = src.WeekStart,
            CurrentOneRmKg     = src.CurrentOneRmKg,
            WeeklyVolumeKg     = src.WeeklyVolumeKg,
            SessionCount       = src.SessionCount,
            OverlapScore       = src.OverlapScore,
            TrainingAgeWeeks   = src.TrainingAgeWeeks,
            BodyweightKg       = src.BodyweightKg,
            AvgDifficultyScore = src.AvgDifficultyScore,
            PrevWeekOneRmKg    = src.PrevWeekOneRmKg,
            PrevWeekVolumeKg   = src.PrevWeekVolumeKg,
            FourWeekTrendSlope = src.FourWeekTrendSlope,
        };

        private static ExerciseFeatureVector BuildColdStartSeed(string athleteId, string exerciseName) =>
            new()
            {
                WeekStart          = FeatureEngineeringService.GetWeekStart(DateTime.UtcNow),
                CurrentOneRmKg     = 60f,
                WeeklyVolumeKg     = 3000f,
                SessionCount       = 2f,
                OverlapScore       = 0.7f,
                TrainingAgeWeeks   = 0f,
                BodyweightKg       = 75f,
                AvgDifficultyScore = 2.5f,
            };

        // ── Inner types ───────────────────────────────────────────────────────

        private class CachedModel
        {
            public ITransformer           Model      { get; set; } = null!;
            public Microsoft.ML.DataViewSchema Schema { get; set; } = null!;
            public float                  RmseKg     { get; set; }
            public string                 DataSource { get; set; } = "cold-start";
            public DateTime               TrainedAt  { get; set; }

            // Model is considered stale after 7 days
            public bool IsStale => DateTime.UtcNow - TrainedAt > TimeSpan.FromDays(7);
        }

        private class ModelMeta
        {
            public float    RmseKg    { get; set; }
            public DateTime TrainedAt { get; set; }
        }
    }
}
