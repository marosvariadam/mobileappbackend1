using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Planning
{
    /// <summary>
    /// Ranks candidate <see cref="TrainingPlan"/>s by expected weekly 1RM
    /// progress. For each planned exercise it builds a
    /// <see cref="WeeklyFeatureVector"/> (preferring the athlete's own history
    /// when <see cref="FeatureEngineeringService"/> is wired in, else
    /// profile-based defaults), runs the trained regressor through
    /// <see cref="IProgressPredictor.Predict"/>, and aggregates the predicted
    /// deltas weighted by <see cref="AthleteProfile.MuscleGroupPriority"/>.
    /// </summary>
    /// <remarks>
    /// Pure w.r.t. the ML pipeline: it only consumes <c>IProgressPredictor</c>
    /// and never retrains or reloads a model. When the predictor isn't ready
    /// (no model on disk yet, e.g. a brand-new environment) or every call
    /// returns null, the service falls back to a deterministic proxy of the
    /// CP-SAT objective (priority-weighted muscle-overlap volume) so the
    /// ranking never collapses to "all zero".
    /// </remarks>
    public class PlanScoringService
    {
        // Default assumptions when the athlete has no logged history. These
        // mirror the neutral fallbacks used elsewhere in FeatureEngineering so
        // the model sees a profile-shaped row, not a zero row.
        private const double DefaultBodyweightKg = 75.0;
        private const int BeginnerAgeWeeks       = 20;
        private const int IntermediateAgeWeeks   = 100;
        private const int AdvancedAgeWeeks       = 300;
        private const double HistoryLookbackDays = 180;

        private readonly IProgressPredictor _predictor;
        private readonly FeatureEngineeringService? _features;

        public PlanScoringService(
            IProgressPredictor predictor,
            FeatureEngineeringService? features = null)
        {
            _predictor = predictor;
            _features = features;
        }

        public readonly record struct ScoredPlan(
            TrainingPlan Plan,
            double Score,
            bool UsedPredictor);

        /// <summary>
        /// Score every candidate and return them sorted descending by score.
        /// </summary>
        public async Task<List<ScoredPlan>> ScoreAsync(
            IReadOnlyList<TrainingPlan> candidates,
            AthleteProfile profile,
            CancellationToken ct = default)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<ScoredPlan>();

            var results = new List<ScoredPlan>(candidates.Count);
            foreach (var plan in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var scored = await ScoreOneAsync(plan, profile, ct);
                results.Add(scored);
            }

            return results
                .OrderByDescending(r => r.Score)
                .ToList();
        }

        // ── Scoring one candidate ───────────────────────────────────────────

        private async Task<ScoredPlan> ScoreOneAsync(
            TrainingPlan plan,
            AthleteProfile profile,
            CancellationToken ct)
        {
            // When the predictor isn't ready at all, there's no point building
            // feature vectors; go straight to the fallback.
            if (!_predictor.IsReady)
                return new ScoredPlan(plan, FallbackScore(plan, profile), UsedPredictor: false);

            double total = 0;
            bool anyPrediction = false;

            foreach (var day in plan.Days)
            {
                var focus = DayFocus(day);
                foreach (var pe in day.Exercises)
                {
                    ct.ThrowIfCancellationRequested();

                    var vector = await BuildVectorAsync(pe, day, focus, profile, ct);
                    var input = ProgressInput.FromFeatures(vector);
                    var prediction = _predictor.Predict(input);
                    if (prediction == null) continue;

                    anyPrediction = true;
                    double weight = PriorityFor(profile, pe.MuscleGroup);
                    total += weight * prediction.PredictedDelta;
                }
            }

            // Predictor claimed ready but every row came back null (e.g. a
            // transient internal error). Fall back rather than return 0.
            if (!anyPrediction)
                return new ScoredPlan(plan, FallbackScore(plan, profile), UsedPredictor: false);

            return new ScoredPlan(plan, total, UsedPredictor: true);
        }

        // ── Feature vector construction ─────────────────────────────────────

        private async Task<WeeklyFeatureVector> BuildVectorAsync(
            PlannedExercise pe,
            PlanDay day,
            string focus,
            AthleteProfile profile,
            CancellationToken ct)
        {
            WeeklyFeatureVector? historical = null;
            if (_features != null && !string.IsNullOrWhiteSpace(profile.AthleteId))
            {
                try
                {
                    var to = DateTime.UtcNow;
                    var from = to.AddDays(-HistoryLookbackDays);
                    var vectors = await _features.BuildWeeklyVectorsAsync(
                        profile.AthleteId, pe.Name, from, to);
                    historical = vectors.LastOrDefault();
                }
                catch
                {
                    // History lookup is best-effort - a transient DB failure
                    // shouldn't block scoring; defaults still produce a usable
                    // feature row.
                    historical = null;
                }
            }

            int avgReps = Math.Max(1, (pe.RepMin + pe.RepMax) / 2);
            int totalReps = Math.Max(0, pe.Sets * avgReps);
            double bodyweight = profile.WeightKg ?? historical?.BodyweightKg ?? DefaultBodyweightKg;
            double est1Rm = historical?.Est1Rm ?? bodyweight;
            // Rough volume proxy: half-bodyweight load as the "lift weight"
            // when there's no history to anchor on. Isolation lifts get less
            // than the compounds via the 0.5 factor on reps already in Sets.
            double volume = historical?.VolumeKg ?? (pe.Sets * (double)avgReps * bodyweight * 0.5);
            int trainingAge = AgeFromLevel(profile.ExperienceLevel);
            bool isBeginner = historical?.IsBeginner ?? (profile.ExperienceLevel == ExperienceLevel.Beginner);

            return new WeeklyFeatureVector
            {
                WeekStart        = DateTime.UtcNow,
                AthleteId        = profile.AthleteId,
                ExerciseName     = pe.Name,
                MuscleGroup      = pe.MuscleGroup,
                Focus            = focus,
                VolumeKg         = volume,
                Est1Rm           = est1Rm,
                AvgRpe           = pe.TargetRpe,
                SetCount         = pe.Sets,
                TotalReps        = totalReps,
                OverlapScore     = MuscleOverlap.GetScore(focus, pe.MuscleGroup),
                BodyweightKg     = bodyweight,
                TrainingAgeWeeks = trainingAge,
                IsBeginner       = isBeginner,
                PrevWeekEst1Rm   = historical?.Est1Rm,
                PrevWeekVolumeKg = historical?.VolumeKg,
            };
        }

        private static int AgeFromLevel(ExperienceLevel level) => level switch
        {
            ExperienceLevel.Beginner     => BeginnerAgeWeeks,
            ExperienceLevel.Intermediate => IntermediateAgeWeeks,
            ExperienceLevel.Advanced     => AdvancedAgeWeeks,
            _                            => BeginnerAgeWeeks,
        };

        private static double PriorityFor(AthleteProfile profile, string muscleGroup)
        {
            if (string.IsNullOrEmpty(muscleGroup)) return 1.0;
            return profile.MuscleGroupPriority.TryGetValue(muscleGroup, out var p)
                ? Math.Clamp(p, 0.5, 1.5)
                : 1.0;
        }

        // ── Day focus heuristic ─────────────────────────────────────────────

        // Choose the MuscleOverlap focus key ("Push" / "Pull" / "Legs" / "Full")
        // based on which pattern of muscle groups dominates the day in weekly
        // sets. This mirrors how FeatureEngineering resolves focus from a
        // TrainingBlock when one exists; plans don't carry blocks, so the day
        // itself is the window.
        private static string DayFocus(PlanDay day)
        {
            int push = 0, pull = 0, legs = 0;
            foreach (var pe in day.Exercises)
            {
                switch (pe.MuscleGroup)
                {
                    case "Chest":
                    case "Shoulders":
                        push += pe.Sets;
                        break;
                    case "Back":
                        pull += pe.Sets;
                        break;
                    case "Legs":
                        legs += pe.Sets;
                        break;
                    // Arms + Core don't lean push vs pull; treat as neutral.
                }
            }

            int max = Math.Max(push, Math.Max(pull, legs));
            if (max == 0) return "Full";
            // Require a clear majority (>50%) to call the day push/pull/legs;
            // otherwise it's a mixed day and "Full" is the honest overlap key.
            int total = push + pull + legs;
            if (2 * max <= total) return "Full";
            if (max == push) return "Push";
            if (max == pull) return "Pull";
            return "Legs";
        }

        // ── CP-SAT-ish fallback ─────────────────────────────────────────────

        // Deterministic proxy used when the predictor is unavailable. It
        // rewards priority-weighted sets on muscle groups that overlap each
        // day's focus - the same intuition as the CP-SAT objective's volume
        // closeness term - without needing the solver's internal value.
        private static double FallbackScore(TrainingPlan plan, AthleteProfile profile)
        {
            double total = 0;
            foreach (var day in plan.Days)
            {
                var focus = DayFocus(day);
                foreach (var pe in day.Exercises)
                {
                    double overlap = MuscleOverlap.GetScore(focus, pe.MuscleGroup);
                    double priority = PriorityFor(profile, pe.MuscleGroup);
                    total += pe.Sets * overlap * priority;
                }
            }
            return total;
        }
    }
}
