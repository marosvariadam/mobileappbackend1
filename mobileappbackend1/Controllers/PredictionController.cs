using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    /// <summary>Returns historical and predicted Est1Rm series for an exercise.</summary>
    [ApiController]
    [Route("api/prediction")]
    [Authorize]
    public class PredictionController : ControllerBase
    {
        private const int MaxWeeksAhead = 26;
        private const int DefaultWeeksAhead = 8;

        private readonly FeatureEngineeringService _featureService;
        private readonly PredictionEngineService   _predictionService;
        private readonly MetricsLogService         _metricsService;
        private readonly TrainingBlockService      _blockService;
        private readonly UserService               _userService;

        public PredictionController(
            FeatureEngineeringService featureService,
            PredictionEngineService predictionService,
            MetricsLogService metricsService,
            TrainingBlockService blockService,
            UserService userService)
        {
            _featureService = featureService;
            _predictionService = predictionService;
            _metricsService = metricsService;
            _blockService = blockService;
            _userService = userService;
        }

        [HttpGet("exercise")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetForSelf(
            [FromQuery] string exerciseName,
            [FromQuery] int weeksAhead = DefaultWeeksAhead,
            [FromQuery] string? focus = null)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            return await BuildResponseAsync(athleteId, exerciseName, weeksAhead, focus);
        }

        [HttpGet("exercise/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetForAthlete(
            string athleteId,
            [FromQuery] string exerciseName,
            [FromQuery] int weeksAhead = DefaultWeeksAhead,
            [FromQuery] string? focus = null)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            return await BuildResponseAsync(athleteId, exerciseName, weeksAhead, focus);
        }


        private async Task<IActionResult> BuildResponseAsync(
            string athleteId, string exerciseName, int weeksAhead, string? focus)
        {
            if (string.IsNullOrWhiteSpace(exerciseName))
                return BadRequest(new { message = "exerciseName is required." });
            if (weeksAhead < 1 || weeksAhead > MaxWeeksAhead)
                return BadRequest(new { message = $"weeksAhead must be between 1 and {MaxWeeksAhead}." });

            // Pull two years of history.
            var to = DateTime.UtcNow;
            var from = to.AddYears(-2);
            var history = await _featureService.BuildWeeklyVectorsAsync(athleteId, exerciseName, from, to);

            if (history.Count == 0)
                return NotFound(new { message = $"No completed sessions for '{exerciseName}' in the last 2 years." });

            var resolvedFocus = !string.IsNullOrWhiteSpace(focus)
                ? focus
                : await ResolveCurrentFocusAsync(athleteId, to);

            var latestMetrics = await _metricsService.GetLatestAsync();
            var modelLoaded = _predictionService.IsReady;
            var rmseKg = latestMetrics?.Rmse ?? 0.0;

            var actualSeries = history
                .Select(v => new
                {
                    weekStart = v.WeekStart,
                    est1Rm    = Math.Round(v.Est1Rm, 2),
                })
                .ToList();

            var predictedSeries = new List<object>();
            if (modelLoaded)
            {
                var current = CloneVector(history[^1]);
                current.Focus = resolvedFocus;
                current.OverlapScore = MuscleOverlap.GetScore(resolvedFocus, current.MuscleGroup);

                for (int step = 1; step <= weeksAhead; step++)
                {
                    var input = ProgressInput.FromFeatures(current);
                    var pred = _predictionService.Predict(input);
                    if (pred == null) break;  // race with reload - bail gracefully

                    var nextEst1Rm = Math.Max(0, current.Est1Rm + pred.PredictedDelta);
                    var nextWeekStart = current.WeekStart.AddDays(7);

                    // Confidence band grows like sqrt(step).
                    var band = rmseKg * Math.Sqrt(step);

                    predictedSeries.Add(new
                    {
                        weekStart        = nextWeekStart,
                        est1Rm           = Math.Round(nextEst1Rm, 2),
                        confidenceLowKg  = Math.Round(Math.Max(0, nextEst1Rm - band), 2),
                        confidenceHighKg = Math.Round(nextEst1Rm + band, 2),
                    });

                    // Advance state for the next iteration.
                    var advanced = CloneVector(current);
                    advanced.WeekStart        = nextWeekStart;
                    advanced.PrevWeekEst1Rm   = current.Est1Rm;
                    advanced.PrevWeekVolumeKg = current.VolumeKg;
                    advanced.Est1Rm           = nextEst1Rm;
                    advanced.TrainingAgeWeeks = current.TrainingAgeWeeks + 1;
                    advanced.IsBeginner       = advanced.TrainingAgeWeeks < 52;
                    advanced.Focus            = resolvedFocus;
                    advanced.OverlapScore     = current.OverlapScore;
                    // Volume / set / rep / RPE carry forward; assumes similar training.
                    current = advanced;
                }
            }

            return Ok(new
            {
                exerciseName    = exerciseName,
                muscleGroup     = history[^1].MuscleGroup,
                weeksAhead      = weeksAhead,
                focus           = resolvedFocus,
                modelLoaded     = modelLoaded,
                modelTrainedAt  = latestMetrics?.CreatedAt,
                modelRmseKg     = Math.Round(rmseKg, 3),
                actual          = actualSeries,
                predicted       = predictedSeries,
            });
        }


        // Find the focus of the block covering "today"; fall back to the most
        // recent past block, then to "Full" when the athlete has no blocks.
        private async Task<string> ResolveCurrentFocusAsync(string athleteId, DateTime asOf)
        {
            var blocks = await _blockService.GetByAthleteAsync(athleteId);
            if (blocks.Count == 0) return "Full";

            var covering = blocks.FirstOrDefault(b =>
                b.StartDate.Date <= asOf.Date && b.EndDate.Date >= asOf.Date);
            if (covering != null) return covering.Focus;

            var mostRecentPast = blocks
                .Where(b => b.EndDate.Date < asOf.Date)
                .OrderByDescending(b => b.EndDate)
                .FirstOrDefault();
            return mostRecentPast?.Focus ?? "Full";
        }

        private static WeeklyFeatureVector CloneVector(WeeklyFeatureVector v) => new()
        {
            WeekStart        = v.WeekStart,
            AthleteId        = v.AthleteId,
            ExerciseName     = v.ExerciseName,
            MuscleGroup      = v.MuscleGroup,
            Focus            = v.Focus,
            VolumeKg         = v.VolumeKg,
            Est1Rm           = v.Est1Rm,
            AvgRpe           = v.AvgRpe,
            SetCount         = v.SetCount,
            TotalReps        = v.TotalReps,
            OverlapScore     = v.OverlapScore,
            BodyweightKg     = v.BodyweightKg,
            TrainingAgeWeeks = v.TrainingAgeWeeks,
            IsBeginner       = v.IsBeginner,
            PrevWeekEst1Rm   = v.PrevWeekEst1Rm,
            PrevWeekVolumeKg = v.PrevWeekVolumeKg,
        };
    }
}
