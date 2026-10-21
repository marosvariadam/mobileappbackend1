using mobileappbackend1.Planning;
using Session = mobileappbackend1.Planning.AutoregulationService.ExerciseSession;
using Rx = mobileappbackend1.Planning.AutoregulationService.Prescription;
using Week = mobileappbackend1.Planning.AutoregulationService.WeekSummary;

namespace mobileappbackend1.Tests
{
    public class AutoregulationServiceTests
    {
        // Helper to centralize the "default plan prescription" so each test
        // can tweak just what matters (upper/lower, rep range, target RPE).
        private static Rx Rx_(int repMin = 5, int repMax = 8, int targetRpe = 8, bool isUpper = true)
            => new(repMin, repMax, targetRpe, isUpper);

        // ── Double progression ─────────────────────────────────────────────

        [Fact]
        public void DoubleProgression_increases_upper_body_by_2_5_kg_when_all_sets_hit_rep_max_at_or_under_target_rpe()
        {
            var last = new Session(DateTime.UtcNow.AddDays(-7), ActualSets: 3, RepsPerSet: 8, WeightKg: 60, Rpe: 7);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8, isUpper: true));
            Assert.Equal(AutoregulationService.RuleOutcome.Increase, s.Outcome);
            Assert.Equal(62.5, s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_increases_lower_body_by_5_kg_when_all_sets_hit_rep_max_at_or_under_target_rpe()
        {
            var last = new Session(DateTime.UtcNow.AddDays(-7), ActualSets: 3, RepsPerSet: 8, WeightKg: 100, Rpe: 8);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8, isUpper: false));
            Assert.Equal(AutoregulationService.RuleOutcome.Increase, s.Outcome);
            Assert.Equal(105, s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_backs_off_5_percent_when_rpe_overshoots_target_by_1_5()
        {
            // target 8, actual 9.5 → back-off branch fires even though reps hit max.
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 8, WeightKg: 100, Rpe: 9.5);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8));
            Assert.Equal(AutoregulationService.RuleOutcome.Decrease, s.Outcome);
            Assert.Equal(AutoregulationService.RoundTo2_5(100 * 0.95), s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_backs_off_5_percent_when_reps_fall_below_rep_min()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 3, WeightKg: 80, Rpe: 7);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8));
            Assert.Equal(AutoregulationService.RuleOutcome.Decrease, s.Outcome);
            Assert.Equal(AutoregulationService.RoundTo2_5(80 * 0.95), s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_holds_when_in_rep_range_but_not_at_max()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 6, WeightKg: 70, Rpe: 8);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8));
            Assert.Equal(AutoregulationService.RuleOutcome.Hold, s.Outcome);
            Assert.Equal(70, s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_still_increases_when_rpe_is_missing_but_reps_hit_max()
        {
            // No RPE logged → treat as "within target" so the athlete isn't
            // punished for forgetting the field.
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 8, WeightKg: 60, Rpe: null);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8, isUpper: true));
            Assert.Equal(AutoregulationService.RuleOutcome.Increase, s.Outcome);
            Assert.Equal(62.5, s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_returns_hold_without_weight_when_no_session_is_logged()
        {
            var s = AutoregulationService.EvaluateDoubleProgression(null, Rx_());
            Assert.Equal(AutoregulationService.RuleOutcome.Hold, s.Outcome);
            Assert.Null(s.NextWeightKg);
        }

        [Fact]
        public void DoubleProgression_prefers_decrease_over_increase_when_both_fire()
        {
            // Reps hit max AND RPE overshoots - the back-off wins; the
            // athlete is clearly not ready to progress even though reps
            // technically landed.
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 8, WeightKg: 100, Rpe: 9.6);
            var s = AutoregulationService.EvaluateDoubleProgression(last, Rx_(5, 8, 8));
            Assert.Equal(AutoregulationService.RuleOutcome.Decrease, s.Outcome);
        }

        // ── RPE-based ──────────────────────────────────────────────────────

        [Fact]
        public void Est1Rm_matches_epley_with_reps_in_reserve()
        {
            // 100 kg × 5 reps @ RPE 8 → effectiveReps = 5 + (10 − 8) = 7.
            // Epley: 100 × (1 + 7/30) = 100 × 1.2333… ≈ 123.33 kg.
            double e1rm = AutoregulationService.Est1Rm(100, 5, 8);
            Assert.InRange(e1rm, 123.0, 123.7);
        }

        [Fact]
        public void RpeBased_prescribes_a_rounded_weight_at_target_reps_and_rpe()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 5, WeightKg: 100, Rpe: 8);
            var s = AutoregulationService.SuggestRpeBased(last, targetReps: 5, targetRpe: 8);
            Assert.Equal(AutoregulationService.RuleOutcome.Increase, s.Outcome);
            Assert.NotNull(s.NextWeightKg);
            // Same reps + RPE → the prescription recovers the input weight (rounded).
            Assert.Equal(100, s.NextWeightKg!.Value, 1);
        }

        [Fact]
        public void RpeBased_rounds_to_2_point_5_kg()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 8, WeightKg: 82.5, Rpe: 7);
            var s = AutoregulationService.SuggestRpeBased(last, targetReps: 5, targetRpe: 8);
            Assert.NotNull(s.NextWeightKg);
            Assert.Equal(0, s.NextWeightKg!.Value % 2.5);
        }

        [Fact]
        public void RpeBased_prescribes_more_weight_at_lower_target_reps_for_same_e1rm()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 8, WeightKg: 80, Rpe: 7);
            var lowRep  = AutoregulationService.SuggestRpeBased(last, targetReps: 3, targetRpe: 8);
            var highRep = AutoregulationService.SuggestRpeBased(last, targetReps: 10, targetRpe: 8);
            Assert.True(lowRep.NextWeightKg > highRep.NextWeightKg,
                $"3-rep set {lowRep.NextWeightKg} should outweigh 10-rep set {highRep.NextWeightKg}.");
        }

        [Fact]
        public void RpeBased_returns_hold_when_rpe_is_missing()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 5, WeightKg: 100, Rpe: null);
            var s = AutoregulationService.SuggestRpeBased(last, targetReps: 5, targetRpe: 8);
            Assert.Equal(AutoregulationService.RuleOutcome.Hold, s.Outcome);
            Assert.Null(s.NextWeightKg);
        }

        [Fact]
        public void RpeBased_returns_hold_when_input_weight_is_zero()
        {
            var last = new Session(DateTime.UtcNow, ActualSets: 3, RepsPerSet: 5, WeightKg: 0, Rpe: 8);
            var s = AutoregulationService.SuggestRpeBased(last, targetReps: 5, targetRpe: 8);
            Assert.Equal(AutoregulationService.RuleOutcome.Hold, s.Outcome);
            Assert.Null(s.NextWeightKg);
        }

        // ── Deload trigger ─────────────────────────────────────────────────

        [Fact]
        public void Deload_fires_on_two_consecutive_weeks_of_e1rm_stagnation_or_drop()
        {
            // w1 125 → w2 125 (flat) → w3 123 (drop). Three-week non-increasing stretch.
            var weeks = new List<Week>
            {
                new(new DateTime(2026,  9, 28, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 125, AvgRpe: 8),
                new(new DateTime(2026, 10,  5, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 125, AvgRpe: 8),
                new(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 123, AvgRpe: 8),
            };
            Assert.True(AutoregulationService.ShouldSuggestDeload(weeks));
        }

        [Fact]
        public void Deload_fires_when_last_week_avg_rpe_is_at_or_above_9_5()
        {
            // Rising e1RM, but the athlete lived at RPE 9.5+ this week.
            var weeks = new List<Week>
            {
                new(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 100, AvgRpe: 9.6),
            };
            Assert.True(AutoregulationService.ShouldSuggestDeload(weeks));
        }

        [Fact]
        public void Deload_does_not_fire_on_rising_e1rm_at_moderate_rpe()
        {
            var weeks = new List<Week>
            {
                new(new DateTime(2026,  9, 28, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 120, AvgRpe: 8),
                new(new DateTime(2026, 10,  5, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 123, AvgRpe: 8),
                new(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 125, AvgRpe: 8),
            };
            Assert.False(AutoregulationService.ShouldSuggestDeload(weeks));
        }

        [Fact]
        public void Deload_does_not_fire_with_fewer_than_three_weeks_of_stagnation_data()
        {
            var weeks = new List<Week>
            {
                new(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 100, AvgRpe: 8),
                new(new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), BestE1Rm: 100, AvgRpe: 8),
            };
            Assert.False(AutoregulationService.ShouldSuggestDeload(weeks));
        }

        [Fact]
        public void Deload_does_not_fire_on_empty_history()
        {
            Assert.False(AutoregulationService.ShouldSuggestDeload(new List<Week>()));
        }

        [Fact]
        public void DeloadSets_drops_volume_by_40_percent_and_keeps_at_least_one_set()
        {
            Assert.Equal(2, AutoregulationService.DeloadSets(4));   // 4 × 0.6 = 2.4 → 2
            Assert.Equal(3, AutoregulationService.DeloadSets(5));   // 5 × 0.6 = 3.0 → 3
            Assert.Equal(1, AutoregulationService.DeloadSets(1));   // floor at 1
            Assert.Equal(1, AutoregulationService.DeloadSets(0));   // guard clause
        }

        // ── Rounding ───────────────────────────────────────────────────────

        [Fact]
        public void RoundTo2_5_rounds_to_nearest_microplate_step()
        {
            Assert.Equal(60.0, AutoregulationService.RoundTo2_5(60.0));
            Assert.Equal(62.5, AutoregulationService.RoundTo2_5(62.3));
            Assert.Equal(62.5, AutoregulationService.RoundTo2_5(61.3));
            Assert.Equal(65.0, AutoregulationService.RoundTo2_5(63.8));
            Assert.Equal(0.0,  AutoregulationService.RoundTo2_5(0.0));
        }
    }
}
