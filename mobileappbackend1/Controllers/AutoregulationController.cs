using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Trainer")]
    public class AutoregulationController : ControllerBase
    {
        // How far back to pull completed workouts when summarizing weekly
        // e1RM / avg RPE for the deload trigger. 4 weeks gives the rule 3
        // historical points plus the current week.
        private const int HistoryLookbackDays = 28;

        private readonly UserService _users;
        private readonly WorkoutService _workouts;
        private readonly PlanService _plans;
        private readonly ExerciseService _exercises;

        public AutoregulationController(
            UserService users,
            WorkoutService workouts,
            PlanService plans,
            ExerciseService exercises)
        {
            _users = users;
            _workouts = workouts;
            _plans = plans;
            _exercises = exercises;
        }

        /// <summary>
        /// GET /api/autoregulation/{athleteId}/next-week — trainer sees one
        /// suggestion per exercise referenced in the athlete's latest
        /// accepted plan, drawn from the last ~4 weeks of logged sessions.
        /// The pure rules live in <see cref="AutoregulationService"/>; this
        /// endpoint only shapes the inputs.
        /// </summary>
        [HttpGet("{athleteId}/next-week")]
        public async Task<IActionResult> GetNextWeek(string athleteId, CancellationToken ct)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(trainerId)) return Unauthorized();

            var athlete = await _users.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete)
                return NotFound(new { message = "Athlete not found." });
            if (athlete.TrainerId != trainerId)
                return Forbid();

            // The latest plan - accepted or materialized - is the one that
            // defines the prescriptions we're autoregulating against.
            var plans = await _plans.GetAcceptedByAthleteIdAsync(athleteId, ct);
            var plan = plans.FirstOrDefault();
            if (plan == null)
                return Ok(new { planId = (string?)null, suggestions = Array.Empty<object>() });

            var now = DateTime.UtcNow;
            var completed = await _workouts.GetCompletedByAthleteAndDateRangeAsync(
                athleteId, now.AddDays(-HistoryLookbackDays), now);

            // Flatten every logged WorkoutExercise into something the pure
            // service can consume. The map key is Name because
            // PlannedExercise.ExerciseId may be null when the plan came from
            // an LLM output that already got anonymized / fixed.
            var historyByExercise = completed
                .Where(w => w.CompletedAt.HasValue)
                .SelectMany(w => w.Exercises
                    .Where(e => e.IsCompleted && e.ActualSets.HasValue && e.ActualRepetitions.HasValue && e.ActualWeightKg.HasValue)
                    .Select(e => new
                    {
                        Key = e.Name.ToLowerInvariant(),
                        Session = new AutoregulationService.ExerciseSession(
                            Date:        w.CompletedAt!.Value,
                            ActualSets:  e.ActualSets!.Value,
                            RepsPerSet:  e.ActualRepetitions!.Value,
                            WeightKg:    e.ActualWeightKg!.Value,
                            Rpe:         e.Rpe),
                    }))
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Session).OrderBy(s => s.Date).ToList());

            var weekSummaries = BuildWeekSummaries(completed);
            bool globalDeload = AutoregulationService.ShouldSuggestDeload(weekSummaries);

            // Resolve upper/lower per exercise so the double-progression rule
            // picks the right bump. "Legs" is the only lower-body group in
            // our seed; everything else counts as upper-body.
            var allExerciseIds = plan.Days
                .SelectMany(d => d.Exercises)
                .Where(e => !string.IsNullOrEmpty(e.ExerciseId))
                .Select(e => e.ExerciseId!)
                .Distinct()
                .ToList();
            var exerciseById = new Dictionary<string, Exercise>();
            foreach (var id in allExerciseIds)
            {
                var ex = await _exercises.GetByIdAsync(id);
                if (ex != null) exerciseById[id] = ex;
            }

            var suggestions = new List<object>();
            var seen = new HashSet<string>();
            foreach (var day in plan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    if (!seen.Add(pe.Name.ToLowerInvariant())) continue;

                    bool isUpper = !string.Equals(pe.MuscleGroup, "Legs", StringComparison.OrdinalIgnoreCase);
                    var rx = new AutoregulationService.Prescription(pe.RepMin, pe.RepMax, pe.TargetRpe, isUpper);

                    historyByExercise.TryGetValue(pe.Name.ToLowerInvariant(), out var hist);
                    var lastSession = hist?.LastOrDefault();

                    var primary = plan.Progression switch
                    {
                        ProgressionScheme.RpeBased => AutoregulationService.SuggestRpeBased(
                            lastSession,
                            targetReps: Math.Max(1, (pe.RepMin + pe.RepMax) / 2),
                            targetRpe:  pe.TargetRpe),
                        _ => AutoregulationService.EvaluateDoubleProgression(lastSession, rx),
                    };

                    var outcome = globalDeload
                        ? new AutoregulationService.Suggestion(
                            primary.NextWeightKg.HasValue
                                ? AutoregulationService.RoundTo2_5(primary.NextWeightKg.Value * 0.9)
                                : null,
                            AutoregulationService.RuleOutcome.Deload,
                            "Deload week suggested (−40% volume, −10% intensity).")
                        : primary;

                    suggestions.Add(new
                    {
                        exerciseId   = pe.ExerciseId,
                        name         = pe.Name,
                        muscleGroup  = pe.MuscleGroup,
                        scheme       = plan.Progression.ToString(),
                        currentReps  = $"{pe.RepMin}-{pe.RepMax}",
                        targetRpe    = pe.TargetRpe,
                        suggestedWeightKg = outcome.NextWeightKg,
                        suggestedSets     = globalDeload ? AutoregulationService.DeloadSets(pe.Sets) : pe.Sets,
                        outcome      = outcome.Outcome.ToString(),
                        reason       = outcome.Reason,
                        lastLogged   = lastSession.HasValue ? new
                        {
                            date     = lastSession.Value.Date,
                            sets     = lastSession.Value.ActualSets,
                            reps     = lastSession.Value.RepsPerSet,
                            weightKg = lastSession.Value.WeightKg,
                            rpe      = lastSession.Value.Rpe,
                        } : null,
                    });
                }
            }

            return Ok(new
            {
                planId        = plan.Id,
                deloadApplied = globalDeload,
                suggestions,
            });
        }

        // Collapse completed workouts into one row per ISO week with the
        // best e1RM across any lift (sufficient for a general deload signal)
        // and the mean RPE across the week.
        private static List<AutoregulationService.WeekSummary> BuildWeekSummaries(List<Workout> completed)
        {
            static DateTime MondayOf(DateTime dt)
            {
                var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                int diff = (int)utc.DayOfWeek - (int)DayOfWeek.Monday;
                if (diff < 0) diff += 7;
                return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-diff);
            }

            var byWeek = completed
                .Where(w => w.CompletedAt.HasValue)
                .SelectMany(w => w.Exercises
                    .Where(e => e.IsCompleted && e.ActualRepetitions.HasValue && e.ActualWeightKg.HasValue && e.Rpe.HasValue)
                    .Select(e => new
                    {
                        Week   = MondayOf(w.CompletedAt!.Value),
                        E1Rm   = AutoregulationService.Est1Rm(e.ActualWeightKg!.Value, e.ActualRepetitions!.Value, e.Rpe!.Value),
                        Rpe    = (double)e.Rpe!.Value,
                    }))
                .GroupBy(x => x.Week)
                .OrderBy(g => g.Key)
                .Select(g => new AutoregulationService.WeekSummary(
                    WeekStart: g.Key,
                    BestE1Rm:  g.Max(x => x.E1Rm),
                    AvgRpe:    g.Average(x => x.Rpe)))
                .ToList();

            return byWeek;
        }
    }
}
