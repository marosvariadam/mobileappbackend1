using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    /// <summary>
    /// Trainer-facing operational endpoints for the progress prediction model.
    /// Gated by the Trainer role for v1 — solo-dev deployment, no separate
    /// admin role yet. Promote to a dedicated admin claim when multi-trainer
    /// deployments arrive.
    /// </summary>
    [ApiController]
    [Route("api/ml")]
    [Authorize(Roles = "Trainer")]
    public class MLController : ControllerBase
    {
        private readonly MLTrainingService _trainingService;
        private readonly MetricsLogService _metricsService;
        private readonly PredictionEngineService _predictionService;

        public MLController(
            MLTrainingService trainingService,
            MetricsLogService metricsService,
            PredictionEngineService predictionService)
        {
            _trainingService = trainingService;
            _metricsService = metricsService;
            _predictionService = predictionService;
        }

        /// <summary>
        /// Force an immediate retrain. Returns the resulting metrics row when
        /// done. Synchronous — under the current dataset size a full train
        /// finishes in seconds. Move to a queued job model when training time
        /// exceeds request timeout.
        /// </summary>
        [HttpPost("retrain")]
        public async Task<IActionResult> Retrain(CancellationToken ct)
        {
            var log = await _trainingService.TrainAndSaveAsync(trigger: "manual", ct);
            return Ok(new
            {
                createdAt         = log.CreatedAt,
                rowCount          = log.RowCount,
                realRowCount      = log.RealRowCount,
                syntheticRowCount = log.SyntheticRowCount,
                rmse              = log.Rmse,
                meanAbsErr        = log.MeanAbsErr,
                rSquared          = log.RSquared,
                durationSeconds   = log.DurationSeconds,
            });
        }

        /// <summary>Most recent training run, plus a "ready" flag for the live model.</summary>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var latest = await _metricsService.GetLatestAsync();
            return Ok(new
            {
                modelLoaded     = _predictionService.IsReady,
                lastTrainedAt   = latest?.CreatedAt,
                lastTrigger     = latest?.Trigger,
                lastRowCount    = latest?.RowCount,
                lastRealRows    = latest?.RealRowCount,
                lastSynthRows   = latest?.SyntheticRowCount,
                lastRmse        = latest?.Rmse,
                lastMeanAbsErr  = latest?.MeanAbsErr,
                lastRSquared    = latest?.RSquared,
                lastDurationSec = latest?.DurationSeconds,
            });
        }

        /// <summary>Recent training history for ops/debugging dashboards.</summary>
        [HttpGet("metrics")]
        public async Task<IActionResult> Metrics([FromQuery] int limit = 20)
        {
            var rows = await _metricsService.GetRecentAsync(Math.Clamp(limit, 1, 200));
            return Ok(rows.Select(r => new
            {
                createdAt         = r.CreatedAt,
                trigger           = r.Trigger,
                rowCount          = r.RowCount,
                realRowCount      = r.RealRowCount,
                syntheticRowCount = r.SyntheticRowCount,
                rmse              = r.Rmse,
                meanAbsErr        = r.MeanAbsErr,
                rSquared          = r.RSquared,
                durationSeconds   = r.DurationSeconds,
            }).ToList());
        }
    }
}
