using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Services;

namespace mobileappbackend1.Tests
{
    public class PlanScoringServiceTests
    {
        // ── Fake predictor ─────────────────────────────────────────────────────

        // Deterministic stand-in for PredictionEngineService. Lets each test
        // pin the model's "ready" flag and shape the predicted delta without
        // touching MLContext, a saved model file, or MongoDB.
        private sealed class FakePredictor : IProgressPredictor
        {
            private readonly Func<ProgressInput, float?> _fn;
            public FakePredictor(bool isReady, Func<ProgressInput, float?> fn)
            {
                IsReady = isReady;
                _fn = fn;
            }

            public bool IsReady { get; }

            public ProgressPrediction? Predict(ProgressInput input)
            {
                var delta = _fn(input);
                return delta.HasValue ? new ProgressPrediction { PredictedDelta = delta.Value } : null;
            }

            public int CallCount { get; private set; }
        }

        // ── Plan fixtures ──────────────────────────────────────────────────────

        private static PlannedExercise Pe(string id, string name, string muscle, int sets, int rpe = 8) =>
            new()
            {
                ExerciseId = id,
                Name = name,
                MuscleGroup = muscle,
                Sets = sets,
                RepMin = 5,
                RepMax = 8,
                TargetRpe = rpe,
                RestSeconds = 90,
            };

        private static TrainingPlan Plan(string title, params PlanDay[] days) => new()
        {
            Id = $"plan-{title}",
            TrainerId = "t1",
            AthleteId = "ath1",
            Title = title,
            Goal = Goal.Hypertrophy,
            Weeks = 4,
            Split = SplitType.FullBody,
            Status = PlanStatus.Draft,
            Progression = ProgressionScheme.DoubleProgression,
            Source = GenerationSource.Constraint,
            Days = days.ToList(),
        };

        private static PlanDay Day(int index, string label, params PlannedExercise[] exercises) => new()
        {
            DayIndex = index,
            Label = label,
            Exercises = exercises.ToList(),
        };

        private static AthleteProfile MakeProfile(Dictionary<string, double>? priorities = null) => new()
        {
            AthleteId = "ath1",
            Goal = Goal.Hypertrophy,
            ExperienceLevel = ExperienceLevel.Intermediate,
            DaysPerWeek = 2,
            SessionMinutes = 90,
            AvailableEquipment = new List<EquipmentType> { EquipmentType.Barbell, EquipmentType.Dumbbell },
            MuscleGroupPriority = priorities ?? new(),
            WeightKg = 80,
        };

        // Plan A — chest-heavy push day with four exercises
        private static TrainingPlan ChestHeavyPlan() => Plan("A",
            Day(0, "Push",
                Pe("bench",   "Bench Press",    "Chest",     4),
                Pe("incline", "Incline Press",  "Chest",     3),
                Pe("ohp",     "Overhead Press", "Shoulders", 3),
                Pe("pushdown","Tricep Pushdown","Arms",      3)));

        // Plan B — leg-heavy day
        private static TrainingPlan LegHeavyPlan() => Plan("B",
            Day(0, "Legs",
                Pe("squat",    "Back Squat",         "Legs", 4),
                Pe("legpress", "Leg Press",          "Legs", 3),
                Pe("legcurl",  "Leg Curl",           "Legs", 3),
                Pe("calf",     "Standing Calf Raise","Legs", 3)));

        // ── Tests ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Returns_empty_when_no_candidates()
        {
            var svc = new PlanScoringService(new FakePredictor(isReady: true, _ => 1f));
            var scored = await svc.ScoreAsync(Array.Empty<TrainingPlan>(), MakeProfile());
            Assert.Empty(scored);
        }

        [Fact]
        public async Task Uses_predictor_when_ready_and_ranks_by_aggregate_delta()
        {
            // Return a per-exercise delta of 1.0 kg for every row. The plan
            // with the greater total set count wins (10 sets vs 10 sets ...
            // actually tied). Give the chest plan a boost to break ties and
            // confirm the ordering follows the aggregate.
            var predictor = new FakePredictor(isReady: true, input =>
                input.MuscleGroup == "Chest" ? 2.0f : 1.0f);
            var svc = new PlanScoringService(predictor);

            var scored = await svc.ScoreAsync(
                new[] { LegHeavyPlan(), ChestHeavyPlan() },
                MakeProfile());

            Assert.Equal(2, scored.Count);
            Assert.True(scored[0].UsedPredictor);
            Assert.True(scored[1].UsedPredictor);
            Assert.Equal("A", scored[0].Plan.Title);   // chest-heavy ranks higher
            Assert.True(scored[0].Score > scored[1].Score);
        }

        [Fact]
        public async Task Falls_back_when_predictor_not_ready()
        {
            // Predictor would return 100 if asked, but IsReady is false so it
            // must never be called; the fallback score (based on overlap +
            // priority * sets) is what gets used.
            var predictor = new FakePredictor(isReady: false, _ => 100f);
            var svc = new PlanScoringService(predictor);

            var scored = await svc.ScoreAsync(new[] { ChestHeavyPlan() }, MakeProfile());

            Assert.Single(scored);
            Assert.False(scored[0].UsedPredictor);
            // Fallback score is strictly positive for a non-empty plan with
            // valid muscle groups (overlap > 0 for Push focus × Chest / Shoulders).
            Assert.True(scored[0].Score > 0);
            // Also under 100 so we know the predictor's fake constant was not used.
            Assert.True(scored[0].Score < 100 * scored[0].Plan.Days.Sum(d => d.Exercises.Count));
        }

        [Fact]
        public async Task Falls_back_when_predictor_returns_null_for_every_row()
        {
            var predictor = new FakePredictor(isReady: true, _ => null);
            var svc = new PlanScoringService(predictor);

            var scored = await svc.ScoreAsync(new[] { ChestHeavyPlan() }, MakeProfile());

            Assert.Single(scored);
            Assert.False(scored[0].UsedPredictor);
            Assert.True(scored[0].Score > 0);
        }

        [Fact]
        public async Task Muscle_group_priority_shifts_the_ranking()
        {
            // Flat per-exercise delta. Without priority weights the two plans
            // tie up to sets. With a 1.5× leg priority the leg plan wins; with
            // a 1.5× chest priority (and 0.5× leg) the chest plan wins.
            var predictor = new FakePredictor(isReady: true, _ => 1.0f);
            var svc = new PlanScoringService(predictor);

            var legPriority = new Dictionary<string, double> { ["Legs"] = 1.5, ["Chest"] = 0.5 };
            var scoredLegsFavored = await svc.ScoreAsync(
                new[] { ChestHeavyPlan(), LegHeavyPlan() },
                MakeProfile(legPriority));
            Assert.Equal("B", scoredLegsFavored[0].Plan.Title);

            var chestPriority = new Dictionary<string, double> { ["Chest"] = 1.5, ["Legs"] = 0.5 };
            var scoredChestFavored = await svc.ScoreAsync(
                new[] { ChestHeavyPlan(), LegHeavyPlan() },
                MakeProfile(chestPriority));
            Assert.Equal("A", scoredChestFavored[0].Plan.Title);
        }

        [Fact]
        public async Task Feature_vector_sees_plan_prescriptions_and_profile()
        {
            // Verify the input the service hands the predictor is well-shaped:
            // non-zero sets, correct muscle group, non-zero overlap for a
            // push-day chest exercise, bodyweight propagated from the profile.
            var seen = new List<ProgressInput>();
            var predictor = new FakePredictor(isReady: true, input =>
            {
                seen.Add(input);
                return 0.5f;
            });
            var svc = new PlanScoringService(predictor);

            await svc.ScoreAsync(new[] { ChestHeavyPlan() }, MakeProfile());

            Assert.NotEmpty(seen);
            Assert.All(seen, i =>
            {
                Assert.True(i.SetCount > 0);
                Assert.True(i.TotalReps > 0);
                Assert.Equal(80f, i.BodyweightKg);
                Assert.InRange(i.AvgRpe, 1, 10);
            });

            var bench = seen.First(i => i.ExerciseName == "Bench Press");
            Assert.Equal("Chest", bench.MuscleGroup);
            Assert.True(bench.OverlapScore > 0.5, $"Push×Chest overlap should be high, got {bench.OverlapScore}");
        }

        [Fact]
        public async Task Candidates_are_sorted_descending()
        {
            // Make each plan's predicted delta scale with its set count so the
            // "bigger" plan always wins. Three plans, three distinct scores,
            // check strict descending order on the returned list.
            var small  = Plan("small",  Day(0, "Full", Pe("x","X","Chest", 1)));
            var medium = Plan("medium", Day(0, "Full", Pe("x","X","Chest", 3)));
            var large  = Plan("large",  Day(0, "Full", Pe("x","X","Chest", 5)));

            var predictor = new FakePredictor(isReady: true, input => input.SetCount);
            var svc = new PlanScoringService(predictor);

            var scored = await svc.ScoreAsync(new[] { small, medium, large }, MakeProfile());

            Assert.Equal(new[] { "large", "medium", "small" },
                scored.Select(s => s.Plan.Title).ToArray());
            Assert.True(scored[0].Score > scored[1].Score);
            Assert.True(scored[1].Score > scored[2].Score);
        }
    }
}
