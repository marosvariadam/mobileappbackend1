using Microsoft.Extensions.Options;
using Microsoft.ML;
using mobileappbackend1.ML;
using mobileappbackend1.Settings;

namespace mobileappbackend1.Services
{
    /// <summary>
    /// Singleton wrapper around the trained LightGBM model. Loads the .zip from
    /// <see cref="MLSettings.ModelPath"/> on startup (if present) and re-loads
    /// whenever the file changes, so retraining is picked up without a restart.
    ///
    /// <see cref="Predict"/> returns <c>null</c> when no model has been trained
    /// yet — callers treat that as "no prediction available" rather than an
    /// error, which keeps the first-run UX sane before any training has
    /// happened.
    /// </summary>
    public class PredictionEngineService : IDisposable
    {
        private readonly MLContext _ml;
        private readonly string _resolvedPath;
        private readonly ILogger<PredictionEngineService> _logger;

        private ITransformer? _model;
        private readonly ReaderWriterLockSlim _lock = new();
        private FileSystemWatcher? _watcher;

        public PredictionEngineService(
            MLContext ml,
            IOptions<MLSettings> settings,
            IHostEnvironment env,
            ILogger<PredictionEngineService> logger)
        {
            _ml = ml;
            _logger = logger;
            _resolvedPath = Path.IsPathRooted(settings.Value.ModelPath)
                ? settings.Value.ModelPath
                : Path.Combine(env.ContentRootPath, settings.Value.ModelPath);

            var dir = Path.GetDirectoryName(_resolvedPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            TryLoad();
            InitWatcher();
        }

        public bool IsReady
        {
            get
            {
                _lock.EnterReadLock();
                try { return _model != null; }
                finally { _lock.ExitReadLock(); }
            }
        }

        /// <summary>Thread-safe inference. Returns null when the model isn't loaded.</summary>
        public ProgressPrediction? Predict(ProgressInput input)
        {
            _lock.EnterReadLock();
            try
            {
                if (_model == null) return null;
                // PredictionEngine is single-threaded; construct per call. Cheap
                // enough for our QPS; upgrade to a pool if latency matters later.
                var engine = _ml.Model.CreatePredictionEngine<ProgressInput, ProgressPrediction>(_model);
                return engine.Predict(input);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prediction failed; treating as unavailable.");
                return null;
            }
            finally { _lock.ExitReadLock(); }
        }

        /// <summary>
        /// Force-reload the model from disk. Called internally on file change
        /// and exposed so the retrain orchestrator can refresh without waiting
        /// for the file-system watcher.
        /// </summary>
        public void Reload() => TryLoad();

        private void TryLoad()
        {
            if (!File.Exists(_resolvedPath))
            {
                _logger.LogInformation(
                    "No model at {Path}; progress predictions disabled until trained.",
                    _resolvedPath);
                return;
            }

            try
            {
                var model = _ml.Model.Load(_resolvedPath, out _);
                _lock.EnterWriteLock();
                try { _model = model; }
                finally { _lock.ExitWriteLock(); }
                _logger.LogInformation("Progress model loaded from {Path}.", _resolvedPath);
            }
            catch (Exception ex)
            {
                // Partial writes during retrain can briefly produce a bad zip;
                // log but don't crash — next Changed event will retry.
                _logger.LogWarning(ex, "Failed to load model from {Path}; will retry on next change.", _resolvedPath);
            }
        }

        private void InitWatcher()
        {
            var dir = Path.GetDirectoryName(_resolvedPath);
            var file = Path.GetFileName(_resolvedPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;

            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => TryLoad();
            _watcher.Changed += (_, _) => TryLoad();
            _watcher.Renamed += (_, _) => TryLoad();
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _lock.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
