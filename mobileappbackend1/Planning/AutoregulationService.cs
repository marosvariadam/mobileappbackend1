namespace mobileappbackend1.Planning
{
    /// <summary>
    /// Pure logic (no Mongo / no HTTP) for next-week load prescription from
    /// an athlete's logged sessions. Three independent rules the controller
    /// can mix:
    /// <list type="bullet">
    ///   <item><see cref="EvaluateDoubleProgression"/>: last session vs the
    ///     plan's rep range + target RPE.</item>
    ///   <item><see cref="SuggestRpeBased"/>: e1RM via Epley with
    ///     reps-in-reserve, then prescribe next week's weight for the target
    ///     reps/RPE pair.</item>
    ///   <item><see cref="ShouldSuggestDeload"/>: two-week e1RM stagnation
    ///     OR a saturated avg RPE last week.</item>
    /// </list>
    /// Each method takes the data it needs and returns a value - no fields,
    /// no state, so unit tests exercise them directly.
    /// </summary>
    public static class AutoregulationService
    {
        // ── Shared types ────────────────────────────────────────────────────

        /// <summary>One completed set, or an aggregate "N sets of reps @ weight" entry.</summary>
        public readonly record struct ExerciseSession(
            DateTime Date,
            int ActualSets,
            int RepsPerSet,
            double WeightKg,
            double? Rpe);

        /// <summary>Rep range + target RPE drawn from the training plan.</summary>
        public readonly record struct Prescription(
            int RepMin,
            int RepMax,
            int TargetRpe,
            bool IsUpperBody);

        /// <summary>Per-week aggregate used by the deload rule.</summary>
        public readonly record struct WeekSummary(
            DateTime WeekStart,
            double BestE1Rm,
            double AvgRpe);

        public enum RuleOutcome { Hold, Increase, Decrease, Deload }

        public readonly record struct Suggestion(
            double? NextWeightKg,
            RuleOutcome Outcome,
            string Reason);

        // Load bump when all sets hit RepMax at/under target RPE. Compounds
        // on upper-body lifts move in smaller plates than lower-body lifts
        // because absolute strength on the bench is a smaller percentage of
        // 1RM than on the squat/deadlift.
        private const double UpperBumpKg = 2.5;
        private const double LowerBumpKg = 5.0;
        private const double DecreaseFraction = 0.95;      // −5% back-off
        private const double RpeOverThresholdOverTarget = 1.5;  // "RPE ≥ target + 1.5"
        private const double DeloadAvgRpeThreshold = 9.5;
        private const double RoundingKg = 2.5;

        // ── Rule 1: double progression ──────────────────────────────────────

        /// <summary>
        /// Rules from docs/ai-plan-generator.md Phase 7:
        /// - All sets hit RepMax at RPE ≤ target → +<see cref="UpperBumpKg"/> kg
        ///   (upper) / +<see cref="LowerBumpKg"/> kg (lower).
        /// - RPE ≥ target + 1.5, OR reps &lt; RepMin → −5%.
        /// - Otherwise, keep the weight (accumulate reps toward RepMax).
        /// </summary>
        public static Suggestion EvaluateDoubleProgression(
            ExerciseSession? last, Prescription rx)
        {
            if (last == null || last.Value.WeightKg <= 0 || last.Value.ActualSets <= 0)
                return new Suggestion(null, RuleOutcome.Hold, "No logged session yet.");

            var s = last.Value;
            double weight = s.WeightKg;
            int reps = s.RepsPerSet;
            double rpe = s.Rpe ?? 0;

            // Decrease wins over increase when both would apply: a session
            // where the athlete grinds past target RPE isn't "ready" to
            // progress even if the rep count technically landed.
            bool decreaseRpe = s.Rpe.HasValue && rpe >= rx.TargetRpe + RpeOverThresholdOverTarget;
            bool decreaseReps = reps < rx.RepMin;
            if (decreaseRpe || decreaseReps)
            {
                var reason = decreaseRpe
                    ? $"RPE {rpe:0.#} ≥ target+1.5 ({rx.TargetRpe + RpeOverThresholdOverTarget:0.#}); back off 5%."
                    : $"Reps {reps} < RepMin {rx.RepMin}; back off 5%.";
                return new Suggestion(RoundTo2_5(weight * DecreaseFraction), RuleOutcome.Decrease, reason);
            }

            // "all sets hit RepMax" collapses to "RepsPerSet >= RepMax"
            // because WorkoutExercise logs a single ActualRepetitions figure
            // for the whole set block (the Phase 7 rule treats a block as
            // one dense attempt). An absent RPE is treated as "within target"
            // so an athlete who logs reps but not RPE still progresses.
            bool allReachedMax = reps >= rx.RepMax;
            bool rpeUnderTarget = !s.Rpe.HasValue || rpe <= rx.TargetRpe;
            if (allReachedMax && rpeUnderTarget)
            {
                double bump = rx.IsUpperBody ? UpperBumpKg : LowerBumpKg;
                return new Suggestion(RoundTo2_5(weight + bump), RuleOutcome.Increase,
                    $"All sets hit {rx.RepMax} reps at RPE ≤ {rx.TargetRpe}; add {bump} kg.");
            }

            return new Suggestion(weight, RuleOutcome.Hold,
                $"Keep {weight:0.#} kg; aim for more reps toward {rx.RepMax}.");
        }

        // ── Rule 2: RPE-based (Epley with reps-in-reserve) ──────────────────

        /// <summary>
        /// Estimate e1RM from (weight, reps, RPE) via Epley on
        /// "effective reps" = reps + reps-in-reserve, where RIR = 10 − RPE.
        /// Then solve the same equation for the weight that puts targetReps
        /// at targetRpe, rounded to 2.5 kg.
        /// </summary>
        public static Suggestion SuggestRpeBased(
            ExerciseSession? last, int targetReps, int targetRpe)
        {
            if (last == null || last.Value.WeightKg <= 0 || last.Value.RepsPerSet <= 0 || last.Value.Rpe is null)
                return new Suggestion(null, RuleOutcome.Hold,
                    "Need weight, reps and RPE on the last session to estimate e1RM.");

            var s = last.Value;
            double e1rm = Est1Rm(s.WeightKg, s.RepsPerSet, s.Rpe!.Value);
            double next = WeightFor(e1rm, targetReps, targetRpe);

            return new Suggestion(RoundTo2_5(next), RuleOutcome.Increase,
                $"e1RM ≈ {e1rm:0.#} kg; prescribe {RoundTo2_5(next):0.#} kg × {targetReps} @ RPE {targetRpe}.");
        }

        // Epley with reps-in-reserve. Weight × (1 + effectiveReps / 30),
        // effectiveReps = reps + (10 − rpe).
        public static double Est1Rm(double weightKg, int reps, double rpe)
        {
            double effective = reps + (10.0 - rpe);
            return weightKg * (1.0 + effective / 30.0);
        }

        // Inverse: given an e1RM, the weight that lands targetReps at targetRpe.
        public static double WeightFor(double e1rm, int targetReps, double targetRpe)
        {
            double effective = targetReps + (10.0 - targetRpe);
            return e1rm / (1.0 + effective / 30.0);
        }

        // ── Rule 3: deload trigger ──────────────────────────────────────────

        /// <summary>
        /// Deload trigger: either
        /// - last 3 weeks of e1RM are non-increasing (two consecutive weeks
        ///   of stagnation or drop), or
        /// - last week's avg RPE ≥ 9.5.
        /// Needs at least three week summaries for the stagnation check;
        /// the avg-RPE check fires with one.
        /// </summary>
        public static bool ShouldSuggestDeload(IReadOnlyList<WeekSummary> weeks)
        {
            if (weeks == null || weeks.Count == 0) return false;

            if (weeks[^1].AvgRpe >= DeloadAvgRpeThreshold) return true;

            if (weeks.Count < 3) return false;
            var w1 = weeks[^3];
            var w2 = weeks[^2];
            var w3 = weeks[^1];
            return w3.BestE1Rm <= w2.BestE1Rm && w2.BestE1Rm <= w1.BestE1Rm;
        }

        /// <summary>
        /// "Suggest deload week (−40% volume)" — scale the prescribed sets
        /// in a plan day for the deload microcycle. Pure helper so the
        /// controller / Flutter editor can preview the lower volume.
        /// </summary>
        public static int DeloadSets(int originalSets)
            => Math.Max(1, (int)Math.Round(originalSets * 0.6));

        // ── Shared ──────────────────────────────────────────────────────────

        /// <summary>Round weight to the nearest 2.5 kg; standard microplate step.</summary>
        public static double RoundTo2_5(double kg) => Math.Round(kg / RoundingKg) * RoundingKg;
    }
}
