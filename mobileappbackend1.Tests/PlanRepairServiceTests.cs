using mobileappbackend1.Models;
using mobileappbackend1.Planning;
using mobileappbackend1.Planning.Llm;

namespace mobileappbackend1.Tests
{
    public class PlanRepairServiceTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────

        private static Exercise Ex(
            string id, string name, string muscle, MovementPattern pattern, bool isCompound,
            IEnumerable<EquipmentType> equipment, IEnumerable<string>? secondary = null) =>
            new()
            {
                Id = id,
                Name = name,
                MuscleGroup = muscle,
                MovementPattern = pattern,
                IsCompound = isCompound,
                MinLevel = ExperienceLevel.Beginner,
                Equipment = equipment.ToList(),
                SecondaryMuscleGroups = (secondary ?? Array.Empty<string>()).ToList(),
            };

        // Minimal library that still lets a Beginner Strength 3-day FullBody
        // plan land inside every muscle group's MEV. Secondary muscles
        // (Shoulders, Arms, Core) ride on the compounds.
        private static List<Exercise> AllowedLibrary() => new()
        {
            Ex("bench",    "Bench Press",   "Chest",     MovementPattern.HorizontalPush, true,  new[] { EquipmentType.Barbell },  new[] { "Shoulders", "Arms" }),
            Ex("incline",  "Incline Press", "Chest",     MovementPattern.HorizontalPush, true,  new[] { EquipmentType.Barbell },  new[] { "Shoulders", "Arms" }),
            Ex("row",      "Barbell Row",   "Back",      MovementPattern.HorizontalPull, true,  new[] { EquipmentType.Barbell },  new[] { "Arms" }),
            Ex("pulldown", "Lat Pulldown",  "Back",      MovementPattern.VerticalPull,   true,  new[] { EquipmentType.Barbell },  new[] { "Arms" }),
            Ex("squat",    "Back Squat",    "Legs",      MovementPattern.Squat,          true,  new[] { EquipmentType.Barbell },  new[] { "Core" }),
            Ex("rdl",      "RDL",           "Legs",      MovementPattern.Hinge,          true,  new[] { EquipmentType.Barbell },  new[] { "Back" }),
            Ex("ohp",      "Overhead Press","Shoulders", MovementPattern.VerticalPush,   true,  new[] { EquipmentType.Barbell },  new[] { "Arms" }),
            Ex("curl",     "Barbell Curl",  "Arms",      MovementPattern.Isolation,      false, new[] { EquipmentType.Barbell }),
            Ex("pushdown", "Tricep Push",   "Arms",      MovementPattern.Isolation,      false, new[] { EquipmentType.Barbell }),
        };

        private static AthleteProfile MakeProfile() => new()
        {
            AthleteId = "507f1f77bcf86cd799439011",
            Goal = Goal.Strength,
            ExperienceLevel = ExperienceLevel.Beginner,
            DaysPerWeek = 3,
            SessionMinutes = 90,
            AvailableEquipment = new List<EquipmentType> { EquipmentType.Barbell },
            SportHistory = "Secret personal story about John Doe",
        };

        // A plan that satisfies PlanValidator for the profile above (zero Errors).
        // Volume per muscle (primary + 0.5 * secondary):
        //   Chest     bench×2 + incline       = 9           (MEV 6, MRV 16) ✓
        //   Back      row×2 + pulldown + 0.5*rdl = 10.5     (MEV 8, MRV 18) ✓
        //   Shoulders ohp×2 + 0.5*(bench×2+incline) = 10.5  (MEV 6, MRV 16) ✓
        //   Legs      squat 4 + rdl 3 + squat 3 = 10        (MEV 8, MRV 18) ✓
        //   Arms      secondary from 8 compounds = 12       (MEV 4, MRV 14) ✓
        // No direct Arms exercises - the compounds carry them below MRV.
        private static TrainingPlan MakeValidPlan() => new()
        {
            AthleteId = "507f1f77bcf86cd799439011",
            Title = "Fixture",
            Goal = Goal.Strength,
            Weeks = 4,
            Split = SplitType.FullBody,
            Status = PlanStatus.Draft,
            Progression = ProgressionScheme.Linear,
            Source = GenerationSource.LlmOnly,
            Days = new List<PlanDay>
            {
                new()
                {
                    DayIndex = 0, Label = "A",
                    Exercises = new()
                    {
                        Pe("bench",   "Bench Press",   "Chest",     3),
                        Pe("row",     "Barbell Row",   "Back",      3),
                        Pe("squat",   "Back Squat",    "Legs",      4),
                        Pe("ohp",     "Overhead Press","Shoulders", 3),
                    }
                },
                new()
                {
                    DayIndex = 1, Label = "B",
                    Exercises = new()
                    {
                        Pe("incline", "Incline Press", "Chest",     3),
                        Pe("pulldown","Lat Pulldown",  "Back",      3),
                        Pe("rdl",     "RDL",           "Legs",      3),
                    }
                },
                new()
                {
                    DayIndex = 2, Label = "C",
                    Exercises = new()
                    {
                        Pe("squat",   "Back Squat",    "Legs",      3),
                        Pe("row",     "Barbell Row",   "Back",      3),
                        Pe("bench",   "Bench Press",   "Chest",     3),
                        Pe("ohp",     "Overhead Press","Shoulders", 3),
                    }
                },
            }
        };

        // An otherwise-valid plan that references one id the library doesn't
        // know, so the validator raises UNKNOWN_EXERCISE. The deterministic
        // fix will swap it for an alternative with the same movement pattern.
        private static TrainingPlan MakeInvalidPlan()
        {
            var plan = MakeValidPlan();
            plan.Days[0].Exercises[0].ExerciseId = "ghost-exercise-9999";
            plan.Days[0].Exercises[0].Name       = "Ghost Lift";
            plan.Days[0].Exercises[0].MuscleGroup = "Chest";
            return plan;
        }

        private static PlannedExercise Pe(
            string id, string name, string muscle, int sets,
            int repMin = 4, int repMax = 6, int rpe = 8, int rest = 90) =>
            new()
            {
                ExerciseId = id,
                Name = name,
                MuscleGroup = muscle,
                Sets = sets,
                RepMin = repMin,
                RepMax = repMax,
                TargetRpe = rpe,
                RestSeconds = rest,
            };

        // ── Precondition: the fixtures are actually valid / invalid ────────

        [Fact]
        public void Fixture_valid_plan_has_zero_validator_errors()
        {
            var library = AllowedLibrary().ToDictionary(e => e.Id!);
            var errors = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), library)
                .Where(v => v.Severity == Severity.Error).ToList();
            Assert.True(errors.Count == 0,
                "Fixture is miscalibrated: " + string.Join(", ", errors.Select(e => e.Code)));
        }

        [Fact]
        public void Fixture_invalid_plan_raises_unknown_exercise()
        {
            var library = AllowedLibrary().ToDictionary(e => e.Id!);
            var errors = PlanValidator.Validate(MakeInvalidPlan(), MakeProfile(), library)
                .Where(v => v.Severity == Severity.Error).ToList();
            Assert.Contains(errors, e => e.Code == ViolationCode.UnknownExercise);
        }

        // ── Happy path: LLM returns a valid plan on the first attempt ──────

        [Fact]
        public async Task Returns_without_repairing_when_first_plan_is_valid()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeValidPlan())),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));

            var result = await service.GenerateAndRepairAsync(MakeProfile(), AllowedLibrary());

            Assert.NotNull(result.Plan);
            Assert.False(result.UsedDeterministicFix);
            Assert.Empty(result.RemainingErrors);
            Assert.Single(result.Log.Attempts);
            Assert.Equal(0, result.Log.RepairAttemptCount);
            Assert.Equal(result.Plan, result.Log.FinalPlanSnapshot);
        }

        // ── Repair recovers after one bad attempt ──────────────────────────

        [Fact]
        public async Task Repairs_on_second_llm_call_when_first_is_invalid()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeInvalidPlan())),
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeValidPlan())),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));

            var result = await service.GenerateAndRepairAsync(MakeProfile(), AllowedLibrary());

            Assert.NotNull(result.Plan);
            Assert.False(result.UsedDeterministicFix);
            Assert.Empty(result.RemainingErrors);
            Assert.Equal(2, result.Log.Attempts.Count);
            Assert.Equal(1, result.Log.RepairAttemptCount);
        }

        // ── Three strikes → deterministic fix (the spec's key invariant) ──

        [Fact]
        public async Task Falls_back_to_deterministic_fix_after_three_invalid_llm_attempts()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeInvalidPlan())),
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeInvalidPlan())),
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeInvalidPlan())),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));

            var result = await service.GenerateAndRepairAsync(MakeProfile(), AllowedLibrary());

            Assert.NotNull(result.Plan);
            Assert.True(result.UsedDeterministicFix);
            Assert.Equal(PlanRepairService.MaxLlmAttempts, result.Log.RepairAttemptCount);

            // 3 LLM attempts + 1 synthetic deterministic-fix attempt row.
            Assert.Equal(PlanRepairService.MaxLlmAttempts + 1, result.Log.Attempts.Count);
            Assert.Equal("[deterministic-fix]", result.Log.Attempts.Last().RawOutput);

            // The offending id must have been swapped out and the plan should
            // no longer carry UNKNOWN_EXERCISE anywhere.
            var remainingIds = result.Plan!.Days.SelectMany(d => d.Exercises).Select(e => e.ExerciseId);
            Assert.DoesNotContain("ghost-exercise-9999", remainingIds);
            Assert.DoesNotContain(result.RemainingErrors,
                v => v.Code == ViolationCode.UnknownExercise);
        }

        // ── Every attempt returns no parseable JSON ────────────────────────

        [Fact]
        public async Task Logs_all_three_attempts_when_llm_never_returns_parseable_json()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.NoToolCall(),
                FakeLlmClient.NoToolCall(),
                FakeLlmClient.NoToolCall(),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));

            var result = await service.GenerateAndRepairAsync(MakeProfile(), AllowedLibrary());

            Assert.True(result.UsedDeterministicFix);
            Assert.Equal(PlanRepairService.MaxLlmAttempts, result.Log.Attempts.Count);
            Assert.All(result.Log.Attempts, a =>
                Assert.Contains(a.Violations, v => v.Code == "LLM_PARSE_FAILED"));
            // No candidate ever existed - nothing to patch.
            Assert.Null(result.Plan);
        }

        // ── Privacy: nothing identifying crosses the LLM boundary ─────────

        [Fact]
        public async Task Anonymizes_profile_before_sending_to_llm()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeValidPlan())),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));
            var profile = MakeProfile();

            await service.GenerateAndRepairAsync(profile, AllowedLibrary());

            var (_, userPrompt, _) = Assert.Single(fake.JsonCalls);
            Assert.DoesNotContain(profile.AthleteId, userPrompt);
            Assert.DoesNotContain("John Doe", userPrompt);
            Assert.DoesNotContain("Secret personal story", userPrompt);
        }

        [Fact]
        public async Task Log_captures_anonymized_profile_snapshot()
        {
            var fake = new FakeLlmClient(new[]
            {
                FakeLlmClient.Ok(FakeLlmClient.ToToolJson(MakeValidPlan())),
            });
            var service = new PlanRepairService(new LlmPlanService(fake));
            var profile = MakeProfile();

            var result = await service.GenerateAndRepairAsync(profile, AllowedLibrary());

            Assert.NotNull(result.Log.ProfileSnapshot);
            Assert.Equal(PlanAnonymizer.AnonymousId, result.Log.ProfileSnapshot!.AthleteId);
            Assert.Null(result.Log.ProfileSnapshot.SportHistory);
            // Training-relevant fields still there for the evaluation harness.
            Assert.Equal(profile.Goal, result.Log.ProfileSnapshot.Goal);
            Assert.Equal(profile.DaysPerWeek, result.Log.ProfileSnapshot.DaysPerWeek);
        }
    }
}
