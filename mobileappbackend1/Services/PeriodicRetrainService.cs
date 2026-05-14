using Microsoft.Extensions.Options;
using mobileappbackend1.Settings;

namespace mobileappbackend1.Services
{
    /// <summary>
    /// Hosted service that retrains the progress model on a fixed cadence
    /// (default weekly per <see cref="MLSettings.RetrainIntervalHours"/>).
    /// On startup, runs an immediate "bootstrap" train if no model file exists
    /// so the prediction endpoint becomes useful before the first scheduled
    /// tick. Otherwise waits the full interval before the first run.
    ///
    /// Drift-triggered off-cycle retrains are not implemented here yet — the
    /// MetricsLog stream this writes to is the foundation for that follow-on.
    /// </summary>
    public class PeriodicRetrainService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MLSettings _settings;
        private readonly IHostEnvironment _env;
        private readonly ILogger<PeriodicRetrainService> _logger;

        public PeriodicRetrainService(
            IServiceScopeFactory scopeFactory,
            IOptions<MLSettings> settings,
            IHostEnvironment env,
            ILogger<PeriodicRetrainService> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings.Value;
            _env = env;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Brief delay so the rest of the app finishes startup (Mongo
            // connections, index creation, etc.) before we hit it.
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }

            var modelPath = ResolveModelPath();
            if (!File.Exists(modelPath))
            {
                _logger.LogInformation("No model file at {Path}; bootstrapping initial train.", modelPath);
                await SafeTrainAsync("bootstrap", ct);
            }

            var interval = TimeSpan.FromHours(Math.Max(1, _settings.RetrainIntervalHours));
            using var timer = new PeriodicTimer(interval);

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                    await SafeTrainAsync("scheduled", ct);
            }
            catch (OperationCanceledException) { /* shutdown */ }
        }

        private async Task SafeTrainAsync(string trigger, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var training = scope.ServiceProvider.GetRequiredService<MLTrainingService>();
                await training.TrainAndSaveAsync(trigger, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Never let a training failure tear down the host.
                _logger.LogError(ex, "Retrain ({Trigger}) failed; will retry next interval.", trigger);
            }
        }

        private string ResolveModelPath() =>
            Path.IsPathRooted(_settings.ModelPath)
                ? _settings.ModelPath
                : Path.Combine(_env.ContentRootPath, _settings.ModelPath);
    }
}
