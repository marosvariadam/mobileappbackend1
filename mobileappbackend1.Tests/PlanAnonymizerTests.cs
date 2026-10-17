using mobileappbackend1.Models;
using mobileappbackend1.Planning.Llm;

namespace mobileappbackend1.Tests
{
    public class PlanAnonymizerTests
    {
        private static AthleteProfile RealProfile() => new()
        {
            Id                  = "507f1f77bcf86cd799439011",
            AthleteId           = "507f1f77bcf86cd799439012",
            Goal                = Goal.Hypertrophy,
            ExperienceLevel     = ExperienceLevel.Intermediate,
            DaysPerWeek         = 4,
            SessionMinutes      = 75,
            AvailableEquipment  = new List<EquipmentType> { EquipmentType.Barbell, EquipmentType.Dumbbell },
            AvoidStressTags     = new List<string> { "knee" },
            ExcludedExerciseIds = new List<string> { "excluded-id-123" },
            MuscleGroupPriority = new Dictionary<string, double> { ["Chest"] = 1.3, ["Legs"] = 1.1 },
            WeightKg            = 82.5,
            SportHistory        = "Rugby for 10 years at Blackheath RFC, moved to London in 2019",
            CreatedAt           = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt           = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc),
        };

        [Fact]
        public void Strips_identifying_fields()
        {
            var anon = PlanAnonymizer.Anonymize(RealProfile());

            Assert.Equal(PlanAnonymizer.AnonymousId, anon.AthleteId);
            Assert.Null(anon.Id);
            Assert.Null(anon.SportHistory);
            Assert.Equal(default, anon.CreatedAt);
            Assert.Null(anon.UpdatedAt);
        }

        [Fact]
        public void Preserves_training_relevant_fields()
        {
            var source = RealProfile();
            var anon = PlanAnonymizer.Anonymize(source);

            Assert.Equal(source.Goal, anon.Goal);
            Assert.Equal(source.ExperienceLevel, anon.ExperienceLevel);
            Assert.Equal(source.DaysPerWeek, anon.DaysPerWeek);
            Assert.Equal(source.SessionMinutes, anon.SessionMinutes);
            Assert.Equal(source.AvailableEquipment, anon.AvailableEquipment);
            Assert.Equal(source.AvoidStressTags, anon.AvoidStressTags);
            Assert.Equal(source.ExcludedExerciseIds, anon.ExcludedExerciseIds);
            Assert.Equal(source.MuscleGroupPriority, anon.MuscleGroupPriority);
            Assert.Equal(source.WeightKg, anon.WeightKg);
        }

        [Fact]
        public void Returns_a_copy_so_mutations_do_not_leak_back()
        {
            var source = RealProfile();
            var anon = PlanAnonymizer.Anonymize(source);

            anon.AvailableEquipment.Add(EquipmentType.Cable);
            anon.AvoidStressTags.Add("shoulder");
            anon.MuscleGroupPriority["Arms"] = 1.5;

            Assert.DoesNotContain(EquipmentType.Cable, source.AvailableEquipment);
            Assert.DoesNotContain("shoulder", source.AvoidStressTags);
            Assert.False(source.MuscleGroupPriority.ContainsKey("Arms"));
        }
    }
}
