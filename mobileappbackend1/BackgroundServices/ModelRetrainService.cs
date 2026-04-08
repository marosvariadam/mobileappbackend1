using mobileappbackend1.Services;
using mobileappbackend1.Services.ML;

namespace mobileappbackend1.BackgroundServices
{
    /// <summary>
    /// Runs weekly (every Sunday ~02:00 UTC) and retrains prediction models for all athletes
    /// who have at least 4 weeks of logged data for a given exercise.
    ///
    /// Failures are logged and skipped — a failed retrain for one athlete never blocks others.
    /// </summary>
    public class ModelRetrainBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory              _scopeFactory;
        private readonly ProgressPredictionService         _predictionService;
        private readonly ILogger<ModelRetrainBackgroundService> _logger;

        // Run 24 h after startup, then every 7 days
        private static readonly TimeSpan InitialDelay  = TimeSpan.FromHours(24);
        private static readonly TimeSpan RetainInterval = TimeSpan.FromDays(7);

        public ModelRetrainBackgroundService(
            IServiceScopeFactory              scopeFactory,
            ProgressPredictionService         predictionService,
            ILogger<ModelRetrainBackgroundService> logger)
        {
            _scopeFactory      = scopeFactory;
            _predictionService = predictionService;
            _logger            = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ModelRetrainBackgroundService started. First run in {Delay}.", InitialDelay);

            // Wait before first run so startup isn't slowed down
            await Task.Delay(InitialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunRetrainCycleAsync(stoppingToken);
                await Task.Delay(RetainInterval, stoppingToken);
            }
        }

        private async Task RunRetrainCycleAsync(CancellationToken ct)
        {
            _logger.LogInformation("Starting weekly ML model retrain cycle.");
            int trained = 0, skipped = 0;

            try
            {
                using var scope      = _scopeFactory.CreateScope();
                var userService      = scope.ServiceProvider.GetRequiredService<UserService>();
                var featureService   = scope.ServiceProvider.GetRequiredService<FeatureEngineeringService>();

                var athletes = await userService.GetAllAthletesAsync();
                _logger.LogInformation("Retraining models for {Count} athletes.", athletes.Count);

                foreach (var athlete in athletes)
                {
                    if (ct.IsCancellationRequested) break;
                    if (athlete.Id is null) continue;

                    try
                    {
                        // Get exercises the athlete has enough history for (≥ 4 real weeks)
                        var trainable = await featureService.GetTrainableExercisesAsync(
                            athlete.Id, minWeeks: 4);

                        foreach (var (exerciseName, _, _) in trainable)
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                await _predictionService.TrainAndSaveAsync(athlete.Id, exerciseName);
                                trained++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex,
                                    "Failed to retrain model for athlete {Id}, exercise {Ex}.",
                                    athlete.Id, exerciseName);
                                skipped++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process athlete {Id} during retrain.", athlete.Id);
                        skipped++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in ML retrain cycle.");
            }

            _logger.LogInformation(
                "ML retrain cycle complete. Trained: {Trained}, Skipped: {Skipped}.",
                trained, skipped);
        }
    }
}
