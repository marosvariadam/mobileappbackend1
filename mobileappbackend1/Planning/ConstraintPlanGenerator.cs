using Google.OrTools.Sat;
using mobileappbackend1.Models;

namespace mobileappbackend1.Planning
{
    /// <summary>
    /// Generates one or more candidate <see cref="TrainingPlan"/>s for a given
    /// <see cref="AthleteProfile"/> using Google OR-Tools' CP-SAT solver. The
    /// generator is pure: it takes a prebuilt exercise library and returns in-
    /// memory plans with no Mongo or HTTP side effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Model formulation.</b> For each day <c>d &#8712; { 0, ..., D-1 }</c>
    /// and each candidate exercise <c>e</c> (after pre-filtering, see below)
    /// the model declares two decision variables:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>x[d,e] &#8712; {0, 1}</c> - is exercise <c>e</c> scheduled
    ///     on day <c>d</c>.</item>
    ///   <item><c>s[d,e] &#8712; {0, 1, ..., 5}</c> - number of sets prescribed
    ///     for exercise <c>e</c> on day <c>d</c>.</item>
    /// </list>
    /// <para>
    /// The two are linked by <c>x[d,e] &lt;= s[d,e] &lt;= 5 * x[d,e]</c>, so a
    /// used exercise always has at least one set and an unused one has zero.
    /// Max-one-per-day is implicit in the booleanness of <c>x</c>.
    /// </para>
    /// <para>
    /// <b>Pre-filtering.</b> The input exercise list is reduced to a candidate
    /// pool by removing any exercise the <see cref="PlanValidator"/> would mark
    /// as an Error regardless of volume: unavailable equipment, stress-tag
    /// conflict with <see cref="AthleteProfile.AvoidStressTags"/>, excluded id,
    /// or <see cref="Exercise.MinLevel"/> above the athlete's level. This keeps
    /// the model small and guarantees the solver cannot pick an invalid lift.
    /// </para>
    /// <para>
    /// <b>Hard constraints.</b> For each day <c>d</c>:
    /// </para>
    /// <list type="number">
    ///   <item><c>4 &lt;= &#8721;<sub>e</sub> x[d,e] &lt;= 8</c> - four to eight
    ///     distinct lifts per session.</item>
    ///   <item><c>&#8721;<sub>e: e.IsCompound</sub> x[d,e] &gt;= 1</c> - at least
    ///     one compound movement per day.</item>
    ///   <item><c>&#8721;<sub>e</sub> s[d,e] &#x22C5; (workSec + restSec(e))
    ///     &lt;= SessionMinutes &#x22C5; 60</c> - session fits within the
    ///     athlete's time budget. <c>workSec</c> is a constant (45 s), <c>restSec</c>
    ///     depends on <c>goal &#x00D7; isCompound</c>.</item>
    /// </list>
    /// <para>
    /// Weekly volume per muscle group <c>m</c> is defined by
    /// <c>V<sub>m</sub> = &#8721;<sub>d,e</sub> contrib(e, m) &#x22C5; s[d,e]</c>
    /// where <c>contrib(e, m) = 2</c> if <c>m</c> is <c>e</c>'s primary
    /// MuscleGroup, <c>1</c> if <c>m</c> appears in SecondaryMuscleGroups,
    /// and <c>0</c> otherwise. The factor-of-two scaling keeps everything
    /// integer (secondary muscles count 0.5 after rescaling). The model then
    /// requires, for every muscle group that has at least one contributing
    /// exercise in the pool:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>2 &#x22C5; MEV<sub>m</sub> &lt;= V<sub>m</sub> &lt;= 2 &#x22C5; MRV<sub>m</sub></c></item>
    /// </list>
    /// <para>with landmarks drawn from <see cref="VolumeLandmarks"/>.</para>
    /// <para>
    /// <b>Objective.</b> Maximize a weighted sum of four terms (all integer-
    /// scaled so CP-SAT can handle them directly):
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Volume closeness.</b> For each muscle <c>m</c>, let
    ///     <c>target<sub>m</sub> = 2 &#x22C5; MAV<sub>m</sub></c> and let
    ///     <c>&#x03B4;<sub>m</sub> = |V<sub>m</sub> - target<sub>m</sub>|</c>.
    ///     Subtract <c>priority<sub>m</sub> &#x22C5; &#x03B4;<sub>m</sub></c>
    ///     where <c>priority<sub>m</sub></c> comes from
    ///     <see cref="AthleteProfile.MuscleGroupPriority"/> scaled by 10 (default 10).
    ///   </item>
    ///   <item><b>Compound bonus.</b> Add <c>CompoundBonus &#x22C5; &#8721; x[d,e]</c>
    ///     over all compound exercises and all days, so the model prefers
    ///     scheduling compounds over isolations when volume targets allow.</item>
    ///   <item><b>Push:pull balance.</b> Subtract <c>BalanceWeight &#x22C5;
    ///     |pushSets - pullSets|</c>, computed by movement pattern over the
    ///     whole microcycle.</item>
    ///   <item><b>Repeat penalty.</b> For every exercise, subtract
    ///     <c>RepeatPenalty &#x22C5; max(0, usedDays - 1)</c> to discourage
    ///     scheduling the same lift on multiple days when alternatives exist.</item>
    /// </list>
    /// <para>
    /// <b>Diversity.</b> To return multiple distinct candidates, each solve
    /// run adds a seeded objective noise term <c>&#8721; noise[d,e] &#x22C5;
    /// x[d,e]</c> with random coefficients in <c>[0, NoiseRange]</c>, plus a
    /// different <c>random_seed</c> parameter for CP-SAT itself. Noise is kept
    /// small relative to the main objective so it only tiebreaks between
    /// near-optimal solutions.
    /// </para>
    /// <para>
    /// <b>Post-processing.</b> Rep ranges, RPE, and rest seconds are assigned
    /// from the athlete's goal and whether each exercise is a compound (see
    /// <see cref="PrescriptionFor"/>). This is deterministic after the solve
    /// and keeps the CP-SAT model purely about selection and set counts.
    /// </para>
    /// </remarks>
    public class ConstraintPlanGenerator
    {
        /// <summary>Assumed per-set work time in seconds, used for the session-length constraint.</summary>
        private const int WorkSecondsPerSet = 45;

        /// <summary>Max sets allowed per (day, exercise) decision variable.</summary>
        private const int MaxSetsPerExercise = 5;

        /// <summary>Minimum exercises scheduled on any one day.</summary>
        private const int MinExercisesPerDay = 4;

        /// <summary>Maximum exercises scheduled on any one day.</summary>
        private const int MaxExercisesPerDay = 8;

        // Objective weights. Scaled integers; relative magnitudes matter more
        // than absolute values. Tuned so volume closeness dominates and
        // compound preference breaks ties.
        private const int CompoundBonus = 3;
        private const int BalanceWeight = 2;
        private const int RepeatPenalty = 4;
        private const int DefaultPriorityScaled = 10;
        private const int NoiseRange = 4;

        /// <summary>Hard wall-clock cap per candidate solve. The model is small enough that this is rarely approached.</summary>
        private const double SolveTimeLimitSeconds = 20.0;

        /// <summary>
        /// Produce up to <paramref name="candidateCount"/> candidate plans.
        /// Returns an empty list when the solver reports infeasibility for the
        /// very first attempt (over-constrained profile / insufficient
        /// equipment); subsequent timeouts just reduce the returned count.
        /// </summary>
        public List<TrainingPlan> Generate(
            AthleteProfile profile,
            IReadOnlyList<Exercise> exercises,
            int candidateCount,
            int seed = 1)
        {
            if (candidateCount < 1) candidateCount = 1;

            var pool = FilterCandidates(profile, exercises);
            if (pool.Count == 0) return new List<TrainingPlan>();

            var plans = new List<TrainingPlan>(candidateCount);
            for (int i = 0; i < candidateCount; i++)
            {
                var plan = SolveOne(profile, pool, candidateIndex: i, seed: seed);
                if (plan != null) plans.Add(plan);
            }
            return plans;
        }

        // ── Filtering ──────────────────────────────────────────────────────────

        /// <summary>
        /// Drop exercises the solver would never be allowed to pick: wrong
        /// equipment, stress tags the athlete avoids, excluded ids, or
        /// MinLevel above the athlete's level. Also drops exercises with no
        /// Id since the plan needs a stable identifier for later lookup.
        /// </summary>
        private static List<Exercise> FilterCandidates(
            AthleteProfile profile, IReadOnlyList<Exercise> exercises)
        {
            var available = new HashSet<EquipmentType>(profile.AvailableEquipment);
            var avoidTags = new HashSet<string>(profile.AvoidStressTags, StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(profile.ExcludedExerciseIds);

            return exercises.Where(e =>
                !string.IsNullOrEmpty(e.Id)
                && !excluded.Contains(e.Id!)
                && (int)e.MinLevel <= (int)profile.ExperienceLevel
                && (available.Count == 0 || e.Equipment.Count == 0 || e.Equipment.Any(available.Contains))
                && !e.StressTags.Any(t => avoidTags.Contains(t))
            ).ToList();
        }

        // ── One CP-SAT solve ───────────────────────────────────────────────────

        /// <summary>
        /// Build and solve the integer program for one candidate. Returns null
        /// if the solver reports infeasibility or no solution within
        /// <see cref="SolveTimeLimitSeconds"/>.
        /// </summary>
        private static TrainingPlan? SolveOne(
            AthleteProfile profile,
            List<Exercise> pool,
            int candidateIndex,
            int seed)
        {
            var model = new CpModel();
            int D = profile.DaysPerWeek;
            int E = pool.Count;
            var rng = new Random(seed + candidateIndex * 101);

            // Decision variables.
            var x = new BoolVar[D, E];
            var s = new IntVar[D, E];
            for (int d = 0; d < D; d++)
            {
                for (int e = 0; e < E; e++)
                {
                    x[d, e] = model.NewBoolVar($"x_{d}_{e}");
                    s[d, e] = model.NewIntVar(0, MaxSetsPerExercise, $"s_{d}_{e}");
                    // Link: s[d,e] <= 5 * x[d,e] and s[d,e] >= x[d,e].
                    model.Add(s[d, e] <= MaxSetsPerExercise * x[d, e]);
                    model.Add(s[d, e] >= x[d, e]);
                }
            }

            // Hard: 4..8 exercises per day, with at least one compound.
            for (int d = 0; d < D; d++)
            {
                var usedThisDay = LinearExpr.Sum(Enumerable.Range(0, E).Select(e => (LinearExpr)x[d, e]));
                model.Add(usedThisDay >= MinExercisesPerDay);
                model.Add(usedThisDay <= MaxExercisesPerDay);

                var compoundsThisDay = LinearExpr.Sum(
                    Enumerable.Range(0, E).Where(e => pool[e].IsCompound).Select(e => (LinearExpr)x[d, e]));
                model.Add(compoundsThisDay >= 1);
            }

            // Hard: session time budget per day.
            int sessionCapSeconds = profile.SessionMinutes * 60;
            for (int d = 0; d < D; d++)
            {
                var terms = new List<LinearExpr>();
                for (int e = 0; e < E; e++)
                {
                    var rx = PrescriptionFor(profile.Goal, pool[e].IsCompound);
                    int perSet = WorkSecondsPerSet + rx.RestSeconds;
                    terms.Add(perSet * s[d, e]);
                }
                model.Add(LinearExpr.Sum(terms) <= sessionCapSeconds);
            }

            // Hard: weekly volume per muscle group in [2*MEV, 2*MRV].
            // Secondary muscles count 0.5, so everything is scaled by 2.
            var musclesInPool = CollectMuscleGroups(pool);
            var volumeExpr = new Dictionary<string, LinearExpr>();
            foreach (var m in musclesInPool)
            {
                var terms = new List<LinearExpr>();
                for (int d = 0; d < D; d++)
                {
                    for (int e = 0; e < E; e++)
                    {
                        int c = MuscleContribScaled(pool[e], m);
                        if (c != 0) terms.Add(c * s[d, e]);
                    }
                }
                var vol = LinearExpr.Sum(terms);
                volumeExpr[m] = vol;

                var range = VolumeLandmarks.For(m, profile.ExperienceLevel);
                model.Add(vol >= 2 * range.Mev);
                model.Add(vol <= 2 * range.Mrv);
            }

            // ── Objective ──────────────────────────────────────────────────────

            var objectiveTerms = new List<LinearExpr>();

            // 1. Volume closeness: -priority * |V_m - 2*MAV_m|
            foreach (var m in musclesInPool)
            {
                var range = VolumeLandmarks.For(m, profile.ExperienceLevel);
                int target = 2 * range.Mav;
                int maxDiff = Math.Max(target, 2 * range.Mrv - target) + 10;
                var diff = model.NewIntVar(-maxDiff, maxDiff, $"diff_{m}");
                model.Add(diff == volumeExpr[m] - target);

                var abs = model.NewIntVar(0, maxDiff, $"abs_{m}");
                model.AddAbsEquality(abs, diff);

                int priority = ScaledPriority(profile, m);
                objectiveTerms.Add(-priority * abs);
            }

            // 2. Compound bonus: + CompoundBonus * sum(x) over compounds.
            for (int d = 0; d < D; d++)
                for (int e = 0; e < E; e++)
                    if (pool[e].IsCompound)
                        objectiveTerms.Add(CompoundBonus * x[d, e]);

            // 3. Push:pull balance: - BalanceWeight * |pushSets - pullSets|.
            var pushTerms = new List<LinearExpr>();
            var pullTerms = new List<LinearExpr>();
            for (int d = 0; d < D; d++)
            {
                for (int e = 0; e < E; e++)
                {
                    if (IsPush(pool[e].MovementPattern)) pushTerms.Add(s[d, e]);
                    else if (IsPull(pool[e].MovementPattern)) pullTerms.Add(s[d, e]);
                }
            }
            if (pushTerms.Count > 0 && pullTerms.Count > 0)
            {
                int maxSets = D * E * MaxSetsPerExercise;
                var balanceDiff = model.NewIntVar(-maxSets, maxSets, "balance_diff");
                model.Add(balanceDiff == LinearExpr.Sum(pushTerms) - LinearExpr.Sum(pullTerms));
                var balanceAbs = model.NewIntVar(0, maxSets, "balance_abs");
                model.AddAbsEquality(balanceAbs, balanceDiff);
                objectiveTerms.Add(-BalanceWeight * balanceAbs);
            }

            // 4. Repeat penalty: -RepeatPenalty * max(0, usedDays - 1) per exercise.
            for (int e = 0; e < E; e++)
            {
                var used = LinearExpr.Sum(Enumerable.Range(0, D).Select(d => (LinearExpr)x[d, e]));
                var overshoot = model.NewIntVar(0, D, $"overshoot_{e}");
                model.AddMaxEquality(overshoot, new[] { used - 1, LinearExpr.Constant(0) });
                objectiveTerms.Add(-RepeatPenalty * overshoot);
            }

            // 5. Diversity noise: + noise[d,e] * x[d,e], seeded per candidate.
            for (int d = 0; d < D; d++)
                for (int e = 0; e < E; e++)
                    objectiveTerms.Add(rng.Next(0, NoiseRange + 1) * x[d, e]);

            model.Maximize(LinearExpr.Sum(objectiveTerms));

            // ── Solve ──────────────────────────────────────────────────────────

            var solver = new CpSolver
            {
                StringParameters = $"max_time_in_seconds:{SolveTimeLimitSeconds};random_seed:{seed + candidateIndex + 1}",
            };
            var status = solver.Solve(model);
            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible) return null;

            return MaterializePlan(profile, pool, x, s, solver, candidateIndex);
        }

        // ── Plan assembly ──────────────────────────────────────────────────────

        /// <summary>
        /// Walk the solved x/s matrices and build the returned
        /// <see cref="TrainingPlan"/>. Rep ranges, RPE, and rest come from
        /// goal + compound classification via <see cref="PrescriptionFor"/>.
        /// </summary>
        private static TrainingPlan MaterializePlan(
            AthleteProfile profile,
            List<Exercise> pool,
            BoolVar[,] x,
            IntVar[,] s,
            CpSolver solver,
            int candidateIndex)
        {
            int D = profile.DaysPerWeek;
            var split = SplitForDays(D);

            var plan = new TrainingPlan
            {
                AthleteId   = profile.AthleteId,
                Title       = $"Generated plan ({candidateIndex + 1})",
                Goal        = profile.Goal,
                Weeks       = 4,
                Split       = split,
                Status      = PlanStatus.Draft,
                Progression = ProgressionFor(profile.Goal),
                Source      = GenerationSource.Constraint,
            };

            for (int d = 0; d < D; d++)
            {
                var day = new PlanDay
                {
                    DayIndex = d,
                    Label = LabelForDay(d, split),
                };

                int order = 0;
                for (int e = 0; e < pool.Count; e++)
                {
                    if (solver.Value(x[d, e]) == 0) continue;

                    int sets = (int)solver.Value(s[d, e]);
                    var rx = PrescriptionFor(profile.Goal, pool[e].IsCompound);

                    day.Exercises.Add(new PlannedExercise
                    {
                        ExerciseId  = pool[e].Id,
                        Name        = pool[e].Name,
                        MuscleGroup = pool[e].MuscleGroup,
                        Sets        = sets,
                        RepMin      = rx.RepMin,
                        RepMax      = rx.RepMax,
                        TargetRpe   = rx.TargetRpe,
                        RestSeconds = rx.RestSeconds,
                        Order       = order++,
                    });
                }

                plan.Days.Add(day);
            }

            return plan;
        }

        // ── Helpers (prescriptions, split, enum branches) ──────────────────────

        /// <summary>
        /// Rep range, RPE target, and rest time by athlete goal and exercise
        /// classification. Compounds use lower reps and longer rest; isolations
        /// use higher reps and shorter rest. Values follow common strength /
        /// hypertrophy literature.
        /// </summary>
        internal static Prescription PrescriptionFor(Goal goal, bool isCompound) => goal switch
        {
            Goal.Strength       => isCompound ? new(3, 6, 8, 180) : new(6, 10, 8,  90),
            Goal.Hypertrophy    => isCompound ? new(5, 8, 8, 120) : new(8, 12, 8,  60),
            Goal.GeneralFitness => isCompound ? new(6, 10, 7, 90) : new(10, 15, 7, 60),
            Goal.FatLoss        => isCompound ? new(8, 12, 7, 60) : new(12, 15, 7, 45),
            _                   => isCompound ? new(5, 8, 8, 90)  : new(10, 12, 7, 60),
        };

        internal readonly record struct Prescription(int RepMin, int RepMax, int TargetRpe, int RestSeconds);

        private static SplitType SplitForDays(int daysPerWeek) => daysPerWeek switch
        {
            <= 3 => SplitType.FullBody,
            4    => SplitType.UpperLower,
            _    => SplitType.PushPullLegs,
        };

        private static ProgressionScheme ProgressionFor(Goal goal) => goal switch
        {
            Goal.Strength    => ProgressionScheme.Linear,
            Goal.Hypertrophy => ProgressionScheme.DoubleProgression,
            _                => ProgressionScheme.RpeBased,
        };

        private static string LabelForDay(int dayIndex, SplitType split) => split switch
        {
            SplitType.FullBody     => $"Day {(char)('A' + dayIndex)}",
            SplitType.UpperLower   => (dayIndex % 2 == 0 ? "Upper " : "Lower ") + (char)('A' + dayIndex / 2),
            SplitType.PushPullLegs => (dayIndex % 3) switch
            {
                0 => "Push " + (char)('A' + dayIndex / 3),
                1 => "Pull " + (char)('A' + dayIndex / 3),
                _ => "Legs " + (char)('A' + dayIndex / 3),
            },
            _ => $"Day {dayIndex + 1}",
        };

        /// <summary>Scaled primary/secondary contribution of an exercise to a muscle group.</summary>
        private static int MuscleContribScaled(Exercise e, string muscle)
        {
            if (string.Equals(e.MuscleGroup, muscle, StringComparison.OrdinalIgnoreCase)) return 2;
            if (e.SecondaryMuscleGroups.Any(sm => string.Equals(sm, muscle, StringComparison.OrdinalIgnoreCase))) return 1;
            return 0;
        }

        /// <summary>Every muscle group touched by at least one exercise in the pool.</summary>
        private static HashSet<string> CollectMuscleGroups(List<Exercise> pool)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in pool)
            {
                if (!string.IsNullOrEmpty(e.MuscleGroup)) set.Add(e.MuscleGroup);
                foreach (var sm in e.SecondaryMuscleGroups) set.Add(sm);
            }
            return set;
        }

        private static int ScaledPriority(AthleteProfile profile, string muscle)
        {
            if (profile.MuscleGroupPriority.TryGetValue(muscle, out var p))
                return Math.Max(1, (int)Math.Round(p * 10));
            return DefaultPriorityScaled;
        }

        private static bool IsPush(MovementPattern p) =>
            p == MovementPattern.HorizontalPush || p == MovementPattern.VerticalPush;

        private static bool IsPull(MovementPattern p) =>
            p == MovementPattern.HorizontalPull || p == MovementPattern.VerticalPull;
    }
}
