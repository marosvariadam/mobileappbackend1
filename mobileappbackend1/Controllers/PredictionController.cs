using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Services;
using mobileappbackend1.Services.ML;

namespace mobileappbackend1.Controllers
{
    /// <summary>
    /// Exposes ML-based exercise progress predictions.
    ///
    /// Authorization:
    ///   – An Athlete can only query their own data.
    ///   – A Trainer can only query athletes they are assigned to.
    /// </summary>
    [ApiController]
    [Route("api/prediction")]
    [Authorize]
    public class PredictionController : ControllerBase
    {
        private readonly ProgressPredictionService  _predictionService;
        private readonly FeatureEngineeringService  _featureService;
        private readonly UserService                _userService;

        public PredictionController(
            ProgressPredictionService  predictionService,
            FeatureEngineeringService  featureService,
            UserService                userService)
        {
            _predictionService = predictionService;
            _featureService    = featureService;
            _userService       = userService;
        }

        // ── GET /api/prediction/{athleteId}/exercise/{exerciseName}?weeks=12 ──

        /// <summary>
        /// Returns actual 1RM history + projected curve with confidence bands,
        /// ready to render as a Flutter chart.
        /// </summary>
        [HttpGet("{athleteId}/exercise/{exerciseName}")]
        public async Task<IActionResult> GetPrediction(
            string athleteId,
            string exerciseName,
            [FromQuery] int weeks = 12)
        {
            var authResult = await AuthorizeAthleteAccessAsync(athleteId);
            if (authResult != null) return authResult;

            weeks = Math.Clamp(weeks, 1, 52);

            var response = await _predictionService.GetPredictionAsync(
                athleteId, exerciseName, weeks);

            return Ok(response);
        }

        // ── GET /api/prediction/{athleteId}/exercises ────────────────────────

        /// <summary>
        /// Lists exercises for which the athlete has enough data for prediction
        /// (≥ 4 weeks of logged sets), together with current estimated 1RM.
        /// </summary>
        [HttpGet("{athleteId}/exercises")]
        public async Task<IActionResult> GetTrainableExercises(string athleteId)
        {
            var authResult = await AuthorizeAthleteAccessAsync(athleteId);
            if (authResult != null) return authResult;

            var exercises = await _featureService.GetTrainableExercisesAsync(athleteId, minWeeks: 4);

            var result = exercises.Select(e => new
            {
                exerciseName  = e.ExerciseName,
                weeksOfData   = e.WeekCount,
                latestOneRmKg = MathF.Round(e.LatestOneRm, 2),
            });

            return Ok(result);
        }

        // ── POST /api/prediction/{athleteId}/retrain ─────────────────────────

        /// <summary>
        /// Manually triggers model retraining for the given athlete and exercise.
        /// Only the athlete's assigned trainer may call this.
        /// </summary>
        [HttpPost("{athleteId}/retrain")]
        public async Task<IActionResult> Retrain(
            string athleteId,
            [FromBody] RetrainRequest request)
        {
            // Only trainers may trigger manual retraining
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var currentRole   = User.FindFirstValue(ClaimTypes.Role);

            if (currentRole != "Trainer")
                return Forbid();

            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null)
                return NotFound("Athlete not found.");

            if (athlete.TrainerId != currentUserId)
                return Forbid();

            await _predictionService.TrainAndSaveAsync(athleteId, request.ExerciseName);

            return Ok(new { message = "Model retrained successfully." });
        }

        // ── Authorization helper ─────────────────────────────────────────────

        /// <summary>
        /// Returns a non-null <see cref="IActionResult"/> (Forbid / NotFound) if the
        /// current user is not allowed to access <paramref name="athleteId"/>'s data;
        /// returns null when access is permitted.
        /// </summary>
        private async Task<IActionResult?> AuthorizeAthleteAccessAsync(string athleteId)
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var currentRole   = User.FindFirstValue(ClaimTypes.Role);

            if (currentRole == "Athlete")
            {
                // Athletes may only query themselves
                if (currentUserId != athleteId)
                    return Forbid();

                return null;
            }

            if (currentRole == "Trainer")
            {
                var athlete = await _userService.GetByIdAsync(athleteId);
                if (athlete == null)
                    return NotFound("Athlete not found.");

                if (athlete.TrainerId != currentUserId)
                    return Forbid();

                return null;
            }

            return Forbid();
        }
    }

    public sealed class RetrainRequest
    {
        public string ExerciseName { get; set; } = string.Empty;
    }
}
