using System.Diagnostics;
using mobileappbackend1.Models;

namespace mobileappbackend1.Planning.Llm
{
    /// <summary>
    /// Drives the LLM + validator repair loop and logs every attempt to a
    /// <see cref="PlanGenerationLog"/>. Returns a plan that has either passed
    /// validation or been patched by a deterministic fix on the last-known
    /// candidate.
    /// </summary>
    /// <remarks>
    /// <para>Flow (as specified in docs/ai-plan-generator.md Phase 5):</para>
    /// <list type="number">
    ///   <item>Call <c>GenerateLlmOnly</c>. Validate the result.</item>
    ///   <item>While Error violations remain and we're under
    ///     <see cref="MaxLlmAttempts"/>, call <c>Repair</c> on the LLM with
    ///     the violation list and re-validate.</item>
    ///   <item>If the loop exits with Errors still present, apply
    ///     <see cref="DeterministicFix"/>: swap offending exercises with the
    ///     best valid alternative sharing the same movement pattern, clamp
    ///     sets, and clamp prescriptions. Record the attempt in the log.</item>
    /// </list>
    /// <para>
    /// The input profile is anonymized before anything crosses the LLM seam.
    /// The log stores the anonymized snapshot, not the real profile.
    /// </para>
    /// </remarks>
    public class PlanRepairService
    {
        public const int MaxLlmAttempts = 3;

        private readonly LlmPlanService _llm;

        public PlanRepairService(LlmPlanService llm)
        {
            _llm = llm;
        }

        public readonly record struct RepairResult(
            TrainingPlan? Plan,
            PlanGenerationLog Log,
            bool UsedDeterministicFix,
            IReadOnlyList<Violation> RemainingErrors);

        public async Task<RepairResult> GenerateAndRepairAsync(
            AthleteProfile profile,
            IReadOnlyList<Exercise> allowedExercises,
            CancellationToken ct = default)
        {
            var anon = PlanAnonymizer.Anonymize(profile);
            var library = BuildLibrary(allowedExercises);
            var log = new PlanGenerationLog
            {
                Mode            = GenerationSource.LlmOnly,
                ProfileSnapshot = anon,
            };

            var stopwatch = Stopwatch.StartNew();
            TrainingPlan? plan = null;
            List<Violation> errors = new();
            int totalInputTokens = 0;
            int totalOutputTokens = 0;

            for (int attempt = 0; attempt < MaxLlmAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                LlmPlanService.LlmPlanResult result = plan == null || errors.Count == 0
                    ? await _llm.GenerateLlmOnlyAsync(anon, allowedExercises, ct)
                    : await _llm.RepairAsync(plan, errors, anon, allowedExercises, ct);

                totalInputTokens  += result.InputTokens;
                totalOutputTokens += result.OutputTokens;

                var attemptRecord = new GenerationAttempt
                {
                    AttemptIndex = attempt,
                    RawOutput    = result.RawOutput,
                };

                if (result.Plan == null)
                {
                    attemptRecord.Violations.Add(new ViolationRecord
                    {
                        Code     = "LLM_PARSE_FAILED",
                        Severity = Severity.Error.ToString(),
                        Message  = "LLM response was missing or did not parse into a TrainingPlan.",
                    });
                    log.Attempts.Add(attemptRecord);

                    // No candidate to validate - treat as "retry from scratch".
                    plan = null;
                    errors = new List<Violation>();
                    continue;
                }

                plan = result.Plan;
                var violations = PlanValidator.Validate(plan, profile, library);
                attemptRecord.Violations = violations.Select(ToRecord).ToList();
                log.Attempts.Add(attemptRecord);

                errors = violations.Where(v => v.Severity == Severity.Error).ToList();
                if (errors.Count == 0)
                {
                    stopwatch.Stop();
                    log.FinalPlanSnapshot   = plan;
                    log.RepairAttemptCount  = attempt;  // repairs = attempts after the first
                    log.LatencyMs           = stopwatch.ElapsedMilliseconds;
                    log.InputTokens         = totalInputTokens;
                    log.OutputTokens        = totalOutputTokens;
                    return new RepairResult(plan, log, UsedDeterministicFix: false,
                                            RemainingErrors: Array.Empty<Violation>());
                }
            }

            // Still broken after MaxLlmAttempts. Fall back to a targeted
            // deterministic fix on the last candidate; if we never got a
            // parseable plan, there's nothing to patch and we return null.
            if (plan != null)
            {
                plan = DeterministicFix(plan, errors, profile, allowedExercises, library);
                var post = PlanValidator.Validate(plan, profile, library);
                log.Attempts.Add(new GenerationAttempt
                {
                    AttemptIndex = MaxLlmAttempts,
                    RawOutput    = "[deterministic-fix]",
                    Violations   = post.Select(ToRecord).ToList(),
                });
                errors = post.Where(v => v.Severity == Severity.Error).ToList();
            }

            stopwatch.Stop();
            log.FinalPlanSnapshot  = plan;
            log.RepairAttemptCount = MaxLlmAttempts;
            log.LatencyMs          = stopwatch.ElapsedMilliseconds;
            log.InputTokens        = totalInputTokens;
            log.OutputTokens       = totalOutputTokens;

            return new RepairResult(plan, log, UsedDeterministicFix: true, RemainingErrors: errors);
        }

        // ── Deterministic fix ───────────────────────────────────────────────

        // Applies targeted patches for each Error the validator flagged:
        //   - exercise-id-related (unknown / equipment / stress / excluded /
        //     level-too-high / duplicate-in-day): swap for the best alternative
        //     with the same MovementPattern and MuscleGroup from the allowed
        //     list; drop the offending row if no alternative exists.
        //   - sets / rep / RPE invariants (INVALID_PRESCRIPTION, SESSION_TOO_LONG,
        //     VOLUME_ABOVE_MRV / VOLUME_BELOW_MEV): clamp sets into [1, 5] and
        //     prescriptions into their legal ranges.
        //
        // Pure w.r.t. the LLM - never calls out again. The result still goes
        // through the validator in the caller so remaining issues surface
        // honestly in the log instead of being hidden.
        internal static TrainingPlan DeterministicFix(
            TrainingPlan plan,
            IReadOnlyList<Violation> errors,
            AthleteProfile profile,
            IReadOnlyList<Exercise> allowed,
            IReadOnlyDictionary<string, Exercise> library)
        {
            var fixedPlan = ClonePlan(plan);
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var day in fixedPlan.Days)
                foreach (var pe in day.Exercises)
                    if (!string.IsNullOrEmpty(pe.ExerciseId)) usedIds.Add(pe.ExerciseId);

            foreach (var err in errors)
            {
                switch (err.Code)
                {
                    case ViolationCode.UnknownExercise:
                    case ViolationCode.EquipmentUnavailable:
                    case ViolationCode.StressTagConflict:
                    case ViolationCode.ExcludedExercise:
                    case ViolationCode.LevelTooHigh:
                    case ViolationCode.DuplicateInDay:
                        SwapOffending(fixedPlan, err, profile, allowed, library, usedIds);
                        break;
                }
            }

            // Blanket clamp pass: fixes INVALID_PRESCRIPTION, VOLUME_*,
            // SESSION_TOO_LONG without having to reason about which day /
            // exercise each error came from.
            foreach (var day in fixedPlan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    pe.Sets        = Math.Clamp(pe.Sets, 1, 5);
                    pe.RepMin      = Math.Clamp(pe.RepMin, 1, 30);
                    pe.RepMax      = Math.Clamp(pe.RepMax, pe.RepMin, 30);
                    pe.TargetRpe   = Math.Clamp(pe.TargetRpe, 5, 10);
                    pe.RestSeconds = Math.Clamp(pe.RestSeconds, 30, 300);
                }
            }

            return fixedPlan;
        }

        private static void SwapOffending(
            TrainingPlan plan,
            Violation err,
            AthleteProfile profile,
            IReadOnlyList<Exercise> allowed,
            IReadOnlyDictionary<string, Exercise> library,
            HashSet<string> usedIds)
        {
            if (!err.DayIndex.HasValue) return;
            var day = plan.Days.FirstOrDefault(d => d.DayIndex == err.DayIndex.Value);
            if (day == null) return;

            PlannedExercise? target = null;
            if (!string.IsNullOrEmpty(err.ExerciseId))
                target = day.Exercises.FirstOrDefault(e => e.ExerciseId == err.ExerciseId);
            target ??= day.Exercises.FirstOrDefault();
            if (target == null) return;

            // Find the best replacement: same movement pattern, same primary
            // muscle group if possible, not already used in this plan.
            var original = target.ExerciseId != null && library.TryGetValue(target.ExerciseId, out var o)
                ? o : null;

            var replacement = allowed
                .Where(e => !string.IsNullOrEmpty(e.Id)
                            && !usedIds.Contains(e.Id!)
                            && (original == null || e.MovementPattern == original.MovementPattern)
                            && (original == null
                                || string.Equals(e.MuscleGroup, original.MuscleGroup, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault()
                ?? allowed.FirstOrDefault(e => !string.IsNullOrEmpty(e.Id) && !usedIds.Contains(e.Id!));

            if (replacement == null)
            {
                // No alternative in the pool; drop the offending row rather
                // than leave an invalid id in the plan.
                day.Exercises.Remove(target);
                return;
            }

            if (!string.IsNullOrEmpty(target.ExerciseId)) usedIds.Remove(target.ExerciseId);
            usedIds.Add(replacement.Id!);

            target.ExerciseId  = replacement.Id;
            target.Name        = replacement.Name;
            target.MuscleGroup = replacement.MuscleGroup;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static Dictionary<string, Exercise> BuildLibrary(IReadOnlyList<Exercise> allowed)
        {
            var dict = new Dictionary<string, Exercise>(allowed.Count);
            foreach (var e in allowed)
                if (!string.IsNullOrEmpty(e.Id)) dict[e.Id!] = e;
            return dict;
        }

        private static ViolationRecord ToRecord(Violation v) => new()
        {
            Code       = v.Code,
            Severity   = v.Severity.ToString(),
            Message    = v.Message,
            DayIndex   = v.DayIndex,
            ExerciseId = v.ExerciseId,
        };

        private static TrainingPlan ClonePlan(TrainingPlan src) => new()
        {
            Id              = src.Id,
            TrainerId       = src.TrainerId,
            AthleteId       = src.AthleteId,
            TrainingBlockId = src.TrainingBlockId,
            Title           = src.Title,
            Goal            = src.Goal,
            Weeks           = src.Weeks,
            Split           = src.Split,
            Status          = src.Status,
            Progression     = src.Progression,
            Source          = src.Source,
            CreatedAt       = src.CreatedAt,
            AcceptedAt      = src.AcceptedAt,
            Days = src.Days.Select(d => new PlanDay
            {
                DayIndex  = d.DayIndex,
                Label     = d.Label,
                Exercises = d.Exercises.Select(e => new PlannedExercise
                {
                    ExerciseId  = e.ExerciseId,
                    Name        = e.Name,
                    MuscleGroup = e.MuscleGroup,
                    Sets        = e.Sets,
                    RepMin      = e.RepMin,
                    RepMax      = e.RepMax,
                    TargetRpe   = e.TargetRpe,
                    RestSeconds = e.RestSeconds,
                    Order       = e.Order,
                    Notes       = e.Notes,
                }).ToList(),
            }).ToList(),
        };
    }
}
