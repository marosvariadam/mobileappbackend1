using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Planning.Llm;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PlanController : ControllerBase
    {
        // Named rate-limit policy applied to the LLM-touching endpoints
        // (generate / refine / explanation). Partitioned per trainer in
        // Program.cs so a slow or stuck trainer can't starve the shared bucket.
        public const string LlmRateLimitPolicy = "plan-llm";

        // Explain's language parameter flows into the LLM prompt. Allow only
        // short locale codes (xx or xx-YY) to foreclose prompt-injection via
        // the field; anything else is rejected before we build the prompt.
        private static readonly Regex LocaleCode =
            new(@"^[a-z]{2}(-[A-Z]{2})?$", RegexOptions.Compiled);

        private readonly PlanService _plans;
        private readonly PlanGenerationLogService _logs;
        private readonly AthleteProfileService _profiles;
        private readonly ExerciseService _exercises;
        private readonly UserService _users;
        private readonly WorkoutService _workouts;
        private readonly TrainingBlockService _blocks;
        private readonly ConstraintPlanGenerator _constraint;
        private readonly PlanScoringService _scoring;
        private readonly PlanRepairService _repair;
        private readonly LlmPlanService _llm;

        public PlanController(
            PlanService plans,
            PlanGenerationLogService logs,
            AthleteProfileService profiles,
            ExerciseService exercises,
            UserService users,
            WorkoutService workouts,
            TrainingBlockService blocks,
            ConstraintPlanGenerator constraint,
            PlanScoringService scoring,
            PlanRepairService repair,
            LlmPlanService llm)
        {
            _plans = plans;
            _logs = logs;
            _profiles = profiles;
            _exercises = exercises;
            _users = users;
            _workouts = workouts;
            _blocks = blocks;
            _constraint = constraint;
            _scoring = scoring;
            _repair = repair;
            _llm = llm;
        }

        // ── Trainer ownership guard (mirrors WorkoutController) ─────────────

        // Returns null when the current user is a trainer and owns the given
        // athlete; otherwise returns the IActionResult the endpoint should
        // return immediately. Centralized so every mutation and read uses the
        // identical check.
        private async Task<(string trainerId, User athlete, IActionResult? deny)> RequireTrainerOwnsAsync(
            string athleteId)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(trainerId)) return (string.Empty, default!, Unauthorized());

            var athlete = await _users.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete)
                return (trainerId, default!, NotFound(new { message = "Athlete not found." }));
            if (athlete.TrainerId != trainerId)
                return (trainerId, athlete, Forbid());

            return (trainerId, athlete, null);
        }

        // Same, resolving the athlete via a plan id. Also pins the ownership
        // to plan.TrainerId directly so a trainer reassignment on the athlete
        // can't retroactively hand this plan to a different trainer: both
        // the current athlete->trainer link AND the plan's own TrainerId
        // must match the caller.
        private async Task<(string trainerId, TrainingPlan plan, IActionResult? deny)> RequireTrainerOwnsPlanAsync(
            string planId)
        {
            var plan = await _plans.GetByIdAsync(planId);
            if (plan == null)
                return (string.Empty, default!, NotFound(new { message = "Plan not found." }));

            var (trainerId, _, deny) = await RequireTrainerOwnsAsync(plan.AthleteId);
            if (deny != null) return (trainerId, plan, deny);
            if (!string.IsNullOrEmpty(plan.TrainerId) && plan.TrainerId != trainerId)
                return (trainerId, plan, Forbid());

            return (trainerId, plan, null);
        }

        // ── POST /api/plan/generate ─────────────────────────────────────────

        [HttpPost("generate")]
        [Authorize(Roles = "Trainer")]
        [EnableRateLimiting(LlmRateLimitPolicy)]
        public async Task<IActionResult> Generate([FromBody] GenerateRequest req, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(req.AthleteId))
                return BadRequest(new { message = "athleteId is required." });
            if (!Enum.TryParse<GenerationSource>(req.Mode, true, out var mode))
                return BadRequest(new { message = "mode must be Constraint, LlmOnly, or Hybrid." });

            var (trainerId, _, deny) = await RequireTrainerOwnsAsync(req.AthleteId);
            if (deny != null) return deny;

            // IDOR guard: the trainer must own the referenced block, and the
            // block must already be assigned to this athlete. Checked before
            // we spend LLM/CPU on generation so a bad id fails fast.
            if (!string.IsNullOrEmpty(req.TrainingBlockId))
            {
                var block = await _blocks.GetByIdAsync(req.TrainingBlockId);
                if (block == null
                    || block.TrainerId != trainerId
                    || block.AthleteId != req.AthleteId)
                    return BadRequest(new { message = "Invalid trainingBlockId." });
            }

            var profile = await _profiles.GetByAthleteIdAsync(req.AthleteId);
            if (profile == null)
                return BadRequest(new { message = "Athlete has no AthleteProfile - fill it in before generating." });

            var exercises = await _exercises.GetVisibleToTrainerAsync(trainerId);
            if (exercises.Count == 0)
                return StatusCode(500, new { message = "No exercises available to the trainer." });

            TrainingPlan? best = null;
            List<TrainingPlan> candidates = new();
            PlanGenerationLog? llmLog = null;

            if (mode == GenerationSource.Constraint || mode == GenerationSource.Hybrid)
                candidates.AddRange(_constraint.Generate(profile, exercises, candidateCount: 3));

            if (mode == GenerationSource.LlmOnly || mode == GenerationSource.Hybrid)
            {
                var result = await _repair.GenerateAndRepairAsync(profile, exercises, ct);
                llmLog = result.Log;
                if (result.Plan != null) candidates.Add(result.Plan);
            }

            if (candidates.Count == 0)
                return StatusCode(503, new { message = "No candidate plan could be produced." });

            var scored = await _scoring.ScoreAsync(candidates, profile, ct);
            best = scored[0].Plan;
            best.TrainerId = trainerId;
            best.AthleteId = req.AthleteId;
            best.Weeks = req.Weeks is > 0 ? req.Weeks.Value : best.Weeks;
            best.TrainingBlockId = req.TrainingBlockId;
            best.Source = mode;
            best.Status = PlanStatus.Draft;

            await _plans.CreateAsync(best, ct);

            // If the LLM ran, link and persist the generation log so the
            // evaluation harness can trace this plan back to its attempts.
            if (llmLog != null)
            {
                llmLog.PlanId = best.Id;
                llmLog.Mode = mode;
                llmLog.PredictedProgressScore = scored[0].Score;
                await _logs.InsertAsync(llmLog, ct);
            }

            var library = exercises.Where(e => !string.IsNullOrEmpty(e.Id))
                                   .ToDictionary(e => e.Id!);
            var violations = PlanValidator.Validate(best, profile, library);

            return Ok(new
            {
                plan             = best,
                predictedScore   = scored[0].Score,
                usedPredictor    = scored[0].UsedPredictor,
                warnings         = violations.Where(v => v.Severity == Severity.Warning).ToList(),
                errors           = violations.Where(v => v.Severity == Severity.Error).ToList(),
                candidatesSummary = scored.Select(s => new
                {
                    title         = s.Plan.Title,
                    score         = s.Score,
                    usedPredictor = s.UsedPredictor,
                    daysCount     = s.Plan.Days.Count,
                }).ToList(),
            });
        }

        // ── POST /api/plan/{id}/refine ──────────────────────────────────────

        [HttpPost("{id}/refine")]
        [Authorize(Roles = "Trainer")]
        [EnableRateLimiting(LlmRateLimitPolicy)]
        public async Task<IActionResult> Refine(
            string id, [FromBody] RefineRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Instruction))
                return BadRequest(new { message = "instruction is required." });

            var (trainerId, plan, deny) = await RequireTrainerOwnsPlanAsync(id);
            if (deny != null) return deny;

            var profile = await _profiles.GetByAthleteIdAsync(plan.AthleteId);
            if (profile == null) return BadRequest(new { message = "Athlete has no AthleteProfile." });

            var exercises = await _exercises.GetVisibleToTrainerAsync(trainerId);
            var anon = PlanAnonymizer.Anonymize(profile);
            var result = await _llm.RefineAsync(plan, req.Instruction, anon, exercises, ct);

            if (result.Plan == null)
                return StatusCode(502, new { message = "LLM did not return a parseable refined plan.", raw = result.RawOutput });

            // Preserve id / trainer / athlete / status - the refined content
            // overwrites days + metadata only.
            var refined = result.Plan;
            await _plans.ReplaceDaysAsync(id, refined.Title, refined.Weeks, refined.Split, refined.Days, ct);

            var updated = (await _plans.GetByIdAsync(id, ct))!;
            var library = exercises.Where(e => !string.IsNullOrEmpty(e.Id))
                                   .ToDictionary(e => e.Id!);
            var violations = PlanValidator.Validate(updated, profile, library);

            return Ok(new
            {
                plan     = updated,
                warnings = violations.Where(v => v.Severity == Severity.Warning).ToList(),
                errors   = violations.Where(v => v.Severity == Severity.Error).ToList(),
                diff     = SummarizeDiff(plan, updated),
            });
        }

        // ── PUT /api/plan/{id} ──────────────────────────────────────────────

        [HttpPut("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> ManualEdit(
            string id, [FromBody] ManualEditRequest req, CancellationToken ct)
        {
            var (trainerId, existing, deny) = await RequireTrainerOwnsPlanAsync(id);
            if (deny != null) return deny;
            if (existing.Status == PlanStatus.Materialized)
                return Conflict(new { message = "Plan is already materialized; create a new draft to change it." });

            await _plans.ReplaceDaysAsync(id, req.Title ?? existing.Title,
                                          req.Weeks ?? existing.Weeks,
                                          req.Split ?? existing.Split,
                                          req.Days ?? existing.Days,
                                          ct);

            var updated = (await _plans.GetByIdAsync(id, ct))!;
            var profile = await _profiles.GetByAthleteIdAsync(existing.AthleteId);
            var exercises = await _exercises.GetVisibleToTrainerAsync(trainerId);
            var library = exercises.Where(e => !string.IsNullOrEmpty(e.Id))
                                   .ToDictionary(e => e.Id!);
            var violations = profile != null
                ? PlanValidator.Validate(updated, profile, library)
                : new List<Violation>();

            return Ok(new
            {
                plan     = updated,
                warnings = violations.Where(v => v.Severity == Severity.Warning).ToList(),
                errors   = violations.Where(v => v.Severity == Severity.Error).ToList(),
            });
        }

        // ── POST /api/plan/{id}/validate ────────────────────────────────────

        [HttpPost("{id}/validate")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Validate(string id)
        {
            var (trainerId, plan, deny) = await RequireTrainerOwnsPlanAsync(id);
            if (deny != null) return deny;

            var profile = await _profiles.GetByAthleteIdAsync(plan.AthleteId);
            if (profile == null) return BadRequest(new { message = "Athlete has no AthleteProfile." });

            var exercises = await _exercises.GetVisibleToTrainerAsync(trainerId);
            var library = exercises.Where(e => !string.IsNullOrEmpty(e.Id))
                                   .ToDictionary(e => e.Id!);
            var violations = PlanValidator.Validate(plan, profile, library);

            return Ok(new
            {
                warnings = violations.Where(v => v.Severity == Severity.Warning).ToList(),
                errors   = violations.Where(v => v.Severity == Severity.Error).ToList(),
            });
        }

        // ── POST /api/plan/{id}/accept ──────────────────────────────────────

        [HttpPost("{id}/accept")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Accept(string id, CancellationToken ct)
        {
            var (_, plan, deny) = await RequireTrainerOwnsPlanAsync(id);
            if (deny != null) return deny;
            if (plan.Status == PlanStatus.Materialized)
                return Conflict(new { message = "Plan already materialized." });

            var now = DateTime.UtcNow;
            await _plans.MarkAcceptedAsync(id, now, ct);

            // Snapshot the trainer-final version into the log for the thesis
            // evaluation (edit distance between generated and accepted).
            var log = await _logs.GetByPlanIdAsync(id, ct);
            if (log != null)
            {
                log.TrainerFinalSnapshot = plan;
                // Rewrite in-place: InsertAsync would create a duplicate row.
                // Simplest path - delete + re-insert would also work; here we
                // just leave it and let a Phase-7-style upsert refactor land
                // once we need multi-field updates on this collection.
                await _logs.InsertAsync(new PlanGenerationLog
                {
                    PlanId               = id,
                    Mode                 = log.Mode,
                    ProfileSnapshot      = log.ProfileSnapshot,
                    FinalPlanSnapshot    = log.FinalPlanSnapshot,
                    TrainerFinalSnapshot = plan,
                    Attempts             = log.Attempts,
                    RepairAttemptCount   = log.RepairAttemptCount,
                    LatencyMs            = log.LatencyMs,
                    InputTokens          = log.InputTokens,
                    OutputTokens         = log.OutputTokens,
                    CreatedAt            = now,
                }, ct);
            }

            var updated = await _plans.GetByIdAsync(id, ct);
            return Ok(updated);
        }

        // ── POST /api/plan/{id}/materialize ─────────────────────────────────

        [HttpPost("{id}/materialize")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Materialize(
            string id, [FromBody] MaterializeRequest req, CancellationToken ct)
        {
            var (trainerId, plan, deny) = await RequireTrainerOwnsPlanAsync(id);
            if (deny != null) return deny;
            if (plan.Status == PlanStatus.Draft)
                return Conflict(new { message = "Accept the plan before materializing it." });
            if (plan.Status == PlanStatus.Materialized)
                return Conflict(new { message = "Plan is already materialized." });

            List<Workout> workouts;
            try
            {
                workouts = PlanMaterializer.Materialize(plan, req.StartDate, trainerId);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            foreach (var w in workouts)
                await _workouts.CreateAsync(w);

            await _plans.MarkMaterializedAsync(id, ct);

            return Ok(new
            {
                planId           = id,
                workoutsCreated  = workouts.Count,
                scheduledWindow  = new
                {
                    from = workouts.First().ScheduledDate,
                    to   = workouts.Last().ScheduledDate,
                },
            });
        }

        // ── GET /api/plan/{id}/explanation ──────────────────────────────────

        [HttpGet("{id}/explanation")]
        [Authorize]
        [EnableRateLimiting(LlmRateLimitPolicy)]
        public async Task<IActionResult> Explain(
            string id, [FromQuery] string language = "en", CancellationToken ct = default)
        {
            if (!LocaleCode.IsMatch(language))
                return BadRequest(new { message = "language must be an ISO locale code like 'en' or 'en-US'." });

            var plan = await _plans.GetByIdAsync(id, ct);
            if (plan == null) return NotFound();

            // Trainer may read any of their plans; athlete may read only
            // accepted plans assigned to them.
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isTrainer = User.IsInRole("Trainer");
            if (isTrainer)
            {
                var (_, _, deny) = await RequireTrainerOwnsAsync(plan.AthleteId);
                if (deny != null) return deny;
            }
            else
            {
                if (plan.AthleteId != userId) return Forbid();
                if (plan.Status == PlanStatus.Draft) return Forbid();
            }

            var text = await _llm.ExplainAsync(plan, language, ct);
            return Ok(new { planId = id, language, explanation = text });
        }

        // ── GET /api/plan/athlete/{athleteId} (trainer) ─────────────────────

        [HttpGet("athlete/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetByAthlete(string athleteId, CancellationToken ct)
        {
            var (_, _, deny) = await RequireTrainerOwnsAsync(athleteId);
            if (deny != null) return deny;

            var plans = await _plans.GetByAthleteIdAsync(athleteId, ct);
            return Ok(plans);
        }

        // ── GET /api/plan/me (athlete) ──────────────────────────────────────

        [HttpGet("me")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetMine(CancellationToken ct)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(athleteId)) return Unauthorized();

            var plans = await _plans.GetAcceptedByAthleteIdAsync(athleteId, ct);
            return Ok(plans);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // Compact human-readable diff for /refine responses so the Flutter
        // client can show "what changed" without re-diffing on the device.
        private static object SummarizeDiff(TrainingPlan before, TrainingPlan after)
        {
            string KeyFor(PlannedExercise p) => $"{p.ExerciseId ?? p.Name}:{p.Sets}x{p.RepMin}-{p.RepMax}@{p.TargetRpe}";

            var beforeByDay = before.Days.ToDictionary(
                d => d.DayIndex,
                d => d.Exercises.Select(KeyFor).ToHashSet());
            var afterByDay = after.Days.ToDictionary(
                d => d.DayIndex,
                d => d.Exercises.Select(KeyFor).ToHashSet());

            return afterByDay.Select(kv =>
            {
                beforeByDay.TryGetValue(kv.Key, out var prev);
                prev ??= new HashSet<string>();
                return new
                {
                    dayIndex = kv.Key,
                    added    = kv.Value.Except(prev).ToList(),
                    removed  = prev.Except(kv.Value).ToList(),
                };
            }).ToList();
        }
    }

    // ── Request DTOs ────────────────────────────────────────────────────────

    public class GenerateRequest
    {
        [Required] public string AthleteId { get; set; } = string.Empty;
        [Required] public string Mode      { get; set; } = "Hybrid";
        [Range(1, 52)] public int? Weeks   { get; set; }
        public string? TrainingBlockId     { get; set; }
    }

    public class RefineRequest
    {
        [Required] [MaxLength(2000)]
        public string Instruction { get; set; } = string.Empty;
    }

    public class ManualEditRequest
    {
        [MaxLength(300)] public string? Title { get; set; }
        [Range(1, 52)]   public int?    Weeks { get; set; }
        public SplitType?               Split { get; set; }
        public List<PlanDay>?           Days  { get; set; }
    }

    public class MaterializeRequest
    {
        [Required] public DateTime StartDate { get; set; }
    }
}
