using mobileappbackend1.ML;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Tests
{
    public class ExerciseSeedTests
    {
        private static readonly HashSet<string> ValidMuscleGroups = new()
        {
            "Chest", "Back", "Shoulders", "Legs", "Arms", "Core"
        };

        [Fact]
        public void Seed_has_roughly_eighty_entries()
        {
            Assert.InRange(ExerciseSeed.Canonical.Count, 70, 120);
        }

        [Fact]
        public void Every_seed_entry_has_required_fields()
        {
            foreach (var ex in ExerciseSeed.Canonical)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Name), $"empty name on seed row");
                Assert.Contains(ex.MuscleGroup, ValidMuscleGroups);
                Assert.NotEmpty(ex.Equipment);
            }
        }

        [Fact]
        public void Seed_names_are_unique()
        {
            var dupes = ExerciseSeed.Canonical
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(dupes);
        }

        [Fact]
        public void Every_movement_pattern_except_carry_has_at_least_one_entry()
        {
            var patternsSeen = ExerciseSeed.Canonical.Select(e => e.MovementPattern).ToHashSet();
            foreach (MovementPattern p in Enum.GetValues<MovementPattern>())
            {
                Assert.Contains(p, patternsSeen);
            }
        }

        [Fact]
        public void Compound_exercises_outnumber_their_muscle_group_isolation_in_most_groups()
        {
            // Sanity: for Chest, Back, Shoulders, Legs there should be several compounds.
            foreach (var mg in new[] { "Chest", "Back", "Shoulders", "Legs" })
            {
                var compounds = ExerciseSeed.Canonical.Count(e => e.MuscleGroup == mg && e.IsCompound);
                Assert.True(compounds >= 3, $"expected ≥3 compound movements for {mg}, got {compounds}");
            }
        }
    }

    public class MuscleOverlapTests
    {
        [Fact]
        public void Push_block_targets_chest_fully()
        {
            Assert.Equal(1.0, MuscleOverlap.GetScore("Push", "Chest"));
        }

        [Fact]
        public void Push_block_barely_targets_legs()
        {
            var score = MuscleOverlap.GetScore("Push", "Legs");
            Assert.InRange(score, 0.0, 0.2);
        }

        [Fact]
        public void Legs_block_targets_legs_fully_and_barely_touches_chest()
        {
            Assert.Equal(1.0, MuscleOverlap.GetScore("Legs", "Legs"));
            Assert.InRange(MuscleOverlap.GetScore("Legs", "Chest"), 0.0, 0.1);
        }

        [Fact]
        public void Unknown_focus_returns_default()
        {
            var score = MuscleOverlap.GetScore("TotallyMadeUpFocus", "Chest");
            Assert.InRange(score, 0.0, 0.2);
        }

        [Fact]
        public void Null_inputs_do_not_throw()
        {
            var _ = MuscleOverlap.GetScore(null, null);
            var __ = MuscleOverlap.GetScore(null, "Chest");
            var ___ = MuscleOverlap.GetScore("Push", null);
        }
    }
}
