using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Services;

namespace mobileappbackend1.Tests
{
    public class ConstraintPlanGeneratorTests
    {
        // ── Fixture helpers ────────────────────────────────────────────────────

        // ExerciseSeed.Canonical rows have no Id (they're pre-persistence).
        // Clone them with synthetic ids so the generator + validator can
        // reference each exercise through a stable key.
        private static (List<Exercise> list, Dictionary<string, Exercise> dict) MakeExerciseLibrary()
        {
            var list = new List<Exercise>();
            for (int i = 0; i < ExerciseSeed.Canonical.Count; i++)
            {
                var src = ExerciseSeed.Canonical[i];
                list.Add(new Exercise
                {
                    Id                    = $"ex-{i:D5}",
                    Name                  = src.Name,
                    MuscleGroup           = src.MuscleGroup,
                    SecondaryMuscleGroups = src.SecondaryMuscleGroups.ToList(),
                    MovementPattern       = src.MovementPattern,
                    IsCompound            = src.IsCompound,
                    MinLevel              = src.MinLevel,
                    Equipment             = src.Equipment.ToList(),
                    StressTags            = src.StressTags.ToList(),
                });
            }
            return (list, list.ToDictionary(e => e.Id!));
        }

        // Realistic profile sampler. Always includes Barbell + Dumbbell +
        // Bodyweight so the compound-per-day and MEV constraints stay
        // feasible for the full tier range; adds optional equipment on top.
        private static AthleteProfile RandomProfile(Random rng, int seed)
        {
            var equipment = new List<EquipmentType>
            {
                EquipmentType.Barbell, EquipmentType.Dumbbell, EquipmentType.Bodyweight
            };
            if (rng.NextDouble() < 0.80) equipment.Add(EquipmentType.Machine);
            if (rng.NextDouble() < 0.70) equipment.Add(EquipmentType.Cable);
            if (rng.NextDouble() < 0.30) equipment.Add(EquipmentType.Kettlebell);
            if (rng.NextDouble() < 0.30) equipment.Add(EquipmentType.Band);

            var stress = new List<string>();
            if (rng.NextDouble() < 0.20) stress.Add(RandomStressTag(rng));

            return new AthleteProfile
            {
                AthleteId           = $"ath-{seed:D4}",
                Goal                = (Goal)rng.Next(0, 4),
                ExperienceLevel     = (ExperienceLevel)rng.Next(0, 3),
                DaysPerWeek         = rng.Next(2, 7),
                SessionMinutes      = rng.Next(75, 121),
                AvailableEquipment  = equipment,
                AvoidStressTags     = stress,
                ExcludedExerciseIds = new List<string>(),
                MuscleGroupPriority = new Dictionary<string, double>(),
                WeightKg            = 60 + rng.NextDouble() * 40,
            };
        }

        private static string RandomStressTag(Random rng) => rng.Next(0, 4) switch
        {
            0 => "knee",
            1 => "shoulder",
            2 => "elbow",
            _ => "wrist",
        };

        // Fingerprint capturing exercise selection + set counts for diversity checks.
        private static string Fingerprint(TrainingPlan plan)
        {
            var parts = plan.Days
                .SelectMany(d => d.Exercises.Select(e => $"{d.DayIndex}:{e.ExerciseId}:{e.Sets}"))
                .OrderBy(s => s, StringComparer.Ordinal);
            return string.Join("|", parts);
        }

        // ── Tests ──────────────────────────────────────────────────────────────

        [Fact]
        public void Generator_produces_valid_plan_for_simple_profile()
        {
            var (list, dict) = MakeExerciseLibrary();
            var profile = new AthleteProfile
            {
                AthleteId          = "ath-smoke",
                Goal               = Goal.Hypertrophy,
                ExperienceLevel    = ExperienceLevel.Intermediate,
                DaysPerWeek        = 4,
                SessionMinutes     = 90,
                AvailableEquipment = new List<EquipmentType>
                {
                    EquipmentType.Barbell, EquipmentType.Dumbbell, EquipmentType.Cable,
                    EquipmentType.Machine, EquipmentType.Bodyweight,
                },
            };

            var plans = new ConstraintPlanGenerator().Generate(profile, list, candidateCount: 1, seed: 1);

            Assert.Single(plans);
            var errors = PlanValidator.Validate(plans[0], profile, dict)
                .Where(v => v.Severity == Severity.Error).ToList();
            Assert.Empty(errors);
            Assert.Equal(profile.DaysPerWeek, plans[0].Days.Count);
            Assert.All(plans[0].Days, d => Assert.InRange(d.Exercises.Count, 4, 8));
            Assert.All(plans[0].Days, d => Assert.Contains(d.Exercises, e =>
                dict.TryGetValue(e.ExerciseId ?? "", out var ex) && ex.IsCompound));
        }

        [Fact]
        public void Generator_produces_valid_plans_for_fifty_random_profiles()
        {
            var (list, dict) = MakeExerciseLibrary();
            var generator = new ConstraintPlanGenerator();
            var rng = new Random(42);

            // The strict requirement: zero validator Errors on any plan the
            // solver DOES return. Infeasibility is tracked separately, because
            // some corner profiles (e.g. 2-day Intermediate with heavy volume
            // targets) are legitimately over-constrained; CP-SAT reporting
            // "no plan" is correct behavior, not a bug.
            var validationFailures = new List<string>();
            int generated = 0;

            for (int i = 0; i < 50; i++)
            {
                var profile = RandomProfile(rng, i);
                var plans = generator.Generate(profile, list, candidateCount: 1, seed: i);
                if (plans.Count == 0) continue;

                generated++;
                var errors = PlanValidator.Validate(plans[0], profile, dict)
                    .Where(v => v.Severity == Severity.Error).ToList();
                if (errors.Count > 0)
                {
                    var codes = string.Join(", ", errors.Select(v => v.Code).Distinct());
                    validationFailures.Add($"profile {i} ({profile.Goal}/{profile.ExperienceLevel}/{profile.DaysPerWeek}d/{profile.SessionMinutes}min): {errors.Count} errors [{codes}]");
                }
            }

            Assert.True(validationFailures.Count == 0,
                $"Every generated plan must have zero Error violations. Failures:\n  - " +
                string.Join("\n  - ", validationFailures));
            Assert.True(generated >= 40,
                $"Expected the solver to produce a plan for most profiles; got {generated}/50.");
        }

        [Fact]
        public void Multiple_candidates_for_same_profile_are_not_identical()
        {
            var (list, _) = MakeExerciseLibrary();
            var profile = new AthleteProfile
            {
                AthleteId          = "ath-div",
                Goal               = Goal.Hypertrophy,
                ExperienceLevel    = ExperienceLevel.Intermediate,
                DaysPerWeek        = 4,
                SessionMinutes     = 90,
                AvailableEquipment = new List<EquipmentType>
                {
                    EquipmentType.Barbell, EquipmentType.Dumbbell, EquipmentType.Cable,
                    EquipmentType.Machine, EquipmentType.Bodyweight,
                },
            };

            var plans = new ConstraintPlanGenerator().Generate(profile, list, candidateCount: 3, seed: 7);

            Assert.Equal(3, plans.Count);
            var fingerprints = plans.Select(Fingerprint).ToList();
            Assert.Equal(fingerprints.Count, fingerprints.Distinct().Count());
        }
    }
}
