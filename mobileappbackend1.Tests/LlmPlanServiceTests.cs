using mobileappbackend1.Models;
using mobileappbackend1.Planning.Llm;

namespace mobileappbackend1.Tests
{
    public class LlmPlanServiceTests
    {
        private static List<Exercise> AllowedExercises() => new()
        {
            new Exercise { Id = "bench",   Name = "Bench Press", MuscleGroup = "Chest", MovementPattern = MovementPattern.HorizontalPush, IsCompound = true, MinLevel = ExperienceLevel.Beginner, Equipment = { EquipmentType.Barbell } },
            new Exercise { Id = "row",     Name = "Barbell Row", MuscleGroup = "Back",  MovementPattern = MovementPattern.HorizontalPull, IsCompound = true, MinLevel = ExperienceLevel.Beginner, Equipment = { EquipmentType.Barbell } },
            new Exercise { Id = "squat",   Name = "Back Squat",  MuscleGroup = "Legs",  MovementPattern = MovementPattern.Squat,          IsCompound = true, MinLevel = ExperienceLevel.Beginner, Equipment = { EquipmentType.Barbell } },
        };

        private static AthleteProfile Profile() => new()
        {
            AthleteId = "ath1",
            Goal = Goal.Hypertrophy,
            ExperienceLevel = ExperienceLevel.Beginner,
            DaysPerWeek = 2,
            SessionMinutes = 60,
            AvailableEquipment = { EquipmentType.Barbell },
        };

        [Fact]
        public async Task Parses_tool_schema_json_into_training_plan()
        {
            const string json = """
                {
                  "title": "LLM Draft",
                  "split": "FullBody",
                  "weeks": 4,
                  "days": [
                    {
                      "dayIndex": 0, "label": "A",
                      "exercises": [
                        { "exerciseId": "bench", "sets": 3, "repMin": 8, "repMax": 12, "targetRpe": 8, "restSeconds": 90 },
                        { "exerciseId": "row",   "sets": 3, "repMin": 8, "repMax": 12, "targetRpe": 8, "restSeconds": 90 }
                      ]
                    }
                  ]
                }
                """;
            var fake = new FakeLlmClient(new[] { FakeLlmClient.Ok(json) });
            var service = new LlmPlanService(fake);

            var result = await service.GenerateLlmOnlyAsync(Profile(), AllowedExercises());

            Assert.NotNull(result.Plan);
            Assert.Equal("LLM Draft", result.Plan!.Title);
            Assert.Equal(SplitType.FullBody, result.Plan.Split);
            Assert.Equal(GenerationSource.LlmOnly, result.Plan.Source);
            Assert.Single(result.Plan.Days);
            Assert.Equal(2, result.Plan.Days[0].Exercises.Count);

            // Name + MuscleGroup are resolved server-side from the id, not
            // taken from the LLM, so the model can't rename a lift.
            var bench = result.Plan.Days[0].Exercises[0];
            Assert.Equal("Bench Press", bench.Name);
            Assert.Equal("Chest", bench.MuscleGroup);
        }

        [Fact]
        public async Task Returns_null_plan_when_llm_omits_tool_call()
        {
            var fake = new FakeLlmClient(new[] { FakeLlmClient.NoToolCall() });
            var service = new LlmPlanService(fake);

            var result = await service.GenerateLlmOnlyAsync(Profile(), AllowedExercises());

            Assert.Null(result.Plan);
            Assert.NotEmpty(result.RawOutput);
        }

        [Fact]
        public async Task Returns_null_plan_when_json_is_malformed()
        {
            var fake = new FakeLlmClient(new[] { FakeLlmClient.Ok("{ this is not valid json") });
            var service = new LlmPlanService(fake);

            var result = await service.GenerateLlmOnlyAsync(Profile(), AllowedExercises());

            Assert.Null(result.Plan);
        }

        [Fact]
        public async Task Prompt_contains_allowed_exercise_ids_and_volume_landmarks()
        {
            var fake = new FakeLlmClient(new[] { FakeLlmClient.NoToolCall() });
            var service = new LlmPlanService(fake);

            await service.GenerateLlmOnlyAsync(Profile(), AllowedExercises());

            var (_, user, _) = Assert.Single(fake.JsonCalls);
            Assert.Contains("bench", user);
            Assert.Contains("Barbell Row", user);
            Assert.Contains("MEV", user);
            Assert.Contains("Chest", user);
        }
    }
}
