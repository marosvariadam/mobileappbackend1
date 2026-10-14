using mobileappbackend1.Models;
using mobileappbackend1.Planning;

namespace mobileappbackend1.Tests
{
    public class PlanValidatorTests
    {
        // ── Fixture helpers ────────────────────────────────────────────────────

        private static Exercise Ex(
            string id,
            string name,
            string muscleGroup,
            MovementPattern pattern,
            bool isCompound,
            ExperienceLevel minLevel,
            IEnumerable<EquipmentType> equipment,
            IEnumerable<string>? secondary = null,
            IEnumerable<string>? stressTags = null)
        {
            return new Exercise
            {
                Id = id,
                Name = name,
                MuscleGroup = muscleGroup,
                MovementPattern = pattern,
                IsCompound = isCompound,
                MinLevel = minLevel,
                Equipment = equipment.ToList(),
                SecondaryMuscleGroups = (secondary ?? Array.Empty<string>()).ToList(),
                StressTags = (stressTags ?? Array.Empty<string>()).ToList(),
            };
        }

        private static IReadOnlyDictionary<string, Exercise> MakeExerciseLibrary()
        {
            var list = new List<Exercise>
            {
                Ex("bench",   "Bench Press",         "Chest",     MovementPattern.HorizontalPush, true,  ExperienceLevel.Beginner,     new[] { EquipmentType.Barbell },    new[] { "Shoulders", "Arms" }, new[] { "shoulder", "elbow" }),
                Ex("incline", "Incline Bench Press", "Chest",     MovementPattern.HorizontalPush, true,  ExperienceLevel.Intermediate, new[] { EquipmentType.Barbell },    new[] { "Shoulders", "Arms" }, new[] { "shoulder", "elbow" }),
                Ex("fly",     "Dumbbell Fly",        "Chest",     MovementPattern.Isolation,      false, ExperienceLevel.Intermediate, new[] { EquipmentType.Dumbbell },   null,                          new[] { "shoulder" }),
                Ex("ohp",     "Overhead Press",      "Shoulders", MovementPattern.VerticalPush,   true,  ExperienceLevel.Intermediate, new[] { EquipmentType.Barbell },    new[] { "Arms" },              new[] { "shoulder" }),
                Ex("lateral", "Lateral Raise",       "Shoulders", MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Dumbbell },   null,                          new[] { "shoulder" }),
                Ex("facepull","Face Pull",           "Shoulders", MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Cable },      null,                          null),
                Ex("row",     "Barbell Row",         "Back",      MovementPattern.HorizontalPull, true,  ExperienceLevel.Intermediate, new[] { EquipmentType.Barbell },    new[] { "Arms" },              new[] { "lower_back" }),
                Ex("pullup",  "Pull-Up",             "Back",      MovementPattern.VerticalPull,   true,  ExperienceLevel.Intermediate, new[] { EquipmentType.Bodyweight }, new[] { "Arms" },              new[] { "shoulder" }),
                Ex("latpull", "Lat Pulldown",        "Back",      MovementPattern.VerticalPull,   true,  ExperienceLevel.Beginner,     new[] { EquipmentType.Cable },      new[] { "Arms" },              null),
                Ex("rdl",     "Romanian Deadlift",   "Back",      MovementPattern.Hinge,          true,  ExperienceLevel.Beginner,     new[] { EquipmentType.Barbell },    new[] { "Legs" },              new[] { "lower_back", "hip" }),
                Ex("squat",   "Back Squat",          "Legs",      MovementPattern.Squat,          true,  ExperienceLevel.Beginner,     new[] { EquipmentType.Barbell },    new[] { "Core" },              new[] { "knee", "lower_back" }),
                Ex("legpress","Leg Press",           "Legs",      MovementPattern.Squat,          true,  ExperienceLevel.Beginner,     new[] { EquipmentType.Machine },    null,                          new[] { "knee" }),
                Ex("legcurl", "Leg Curl",            "Legs",      MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Machine },    null,                          null),
                Ex("calf",    "Standing Calf Raise", "Legs",      MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Machine },    null,                          null),
                Ex("curl",    "Barbell Curl",        "Arms",      MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Barbell },    null,                          new[] { "elbow" }),
                Ex("pushdown","Tricep Pushdown",     "Arms",      MovementPattern.Isolation,      false, ExperienceLevel.Beginner,     new[] { EquipmentType.Cable },      null,                          new[] { "elbow" }),
                Ex("advanced_only","Deficit Deadlift", "Back",    MovementPattern.Hinge,          true,  ExperienceLevel.Advanced,     new[] { EquipmentType.Barbell },    null,                          new[] { "lower_back" }),
            };
            return list.ToDictionary(e => e.Id!);
        }

        private static AthleteProfile MakeProfile()
        {
            return new AthleteProfile
            {
                AthleteId = "ath1",
                Goal = Goal.Strength,
                ExperienceLevel = ExperienceLevel.Intermediate,
                DaysPerWeek = 3,
                SessionMinutes = 90,
                AvailableEquipment = new List<EquipmentType>
                {
                    EquipmentType.Barbell, EquipmentType.Dumbbell, EquipmentType.Cable,
                    EquipmentType.Machine, EquipmentType.Bodyweight, EquipmentType.Kettlebell, EquipmentType.Band
                },
                AvoidStressTags = new List<string>(),
                ExcludedExerciseIds = new List<string>(),
                MuscleGroupPriority = new Dictionary<string, double>(),
            };
        }

        private static PlannedExercise Pe(
            string id, string name, string muscleGroup,
            int sets, int repMin, int repMax, int rpe, int rest = 90, int order = 0)
        {
            return new PlannedExercise
            {
                ExerciseId = id,
                Name = name,
                MuscleGroup = muscleGroup,
                Sets = sets,
                RepMin = repMin,
                RepMax = repMax,
                TargetRpe = rpe,
                RestSeconds = rest,
                Order = order,
            };
        }

        // 3-day PPL, Intermediate Strength, every muscle in [MEV, MRV],
        // push:pull ratio 1.0, session times well under 90 min, no duplicates,
        // all rep/set/RPE prescriptions valid, no excluded or stress-tagged conflicts.
        private static TrainingPlan MakeValidPlan()
        {
            return new TrainingPlan
            {
                Id = "plan1",
                TrainerId = "t1",
                AthleteId = "ath1",
                Title = "Baseline",
                Goal = Goal.Strength,
                Weeks = 4,
                Split = SplitType.PushPullLegs,
                Status = PlanStatus.Draft,
                Progression = ProgressionScheme.DoubleProgression,
                Source = GenerationSource.Manual,
                Days = new List<PlanDay>
                {
                    new PlanDay
                    {
                        DayIndex = 0, Label = "Push",
                        Exercises = new List<PlannedExercise>
                        {
                            Pe("bench",    "Bench Press",         "Chest",     4, 4, 6, 8),
                            Pe("ohp",      "Overhead Press",      "Shoulders", 3, 4, 6, 8),
                            Pe("incline",  "Incline Bench Press", "Chest",     3, 4, 6, 8),
                            Pe("lateral",  "Lateral Raise",       "Shoulders", 3, 8, 12, 8, 60),
                            Pe("pushdown", "Tricep Pushdown",     "Arms",      3, 8, 12, 8, 60),
                            Pe("fly",      "Dumbbell Fly",        "Chest",     3, 8, 12, 8, 60),
                        }
                    },
                    new PlanDay
                    {
                        DayIndex = 1, Label = "Pull",
                        Exercises = new List<PlannedExercise>
                        {
                            Pe("row",      "Barbell Row",   "Back",      4, 4, 6, 8),
                            Pe("pullup",   "Pull-Up",       "Back",      3, 4, 6, 8),
                            Pe("latpull",  "Lat Pulldown",  "Back",      3, 8, 12, 8),
                            Pe("facepull", "Face Pull",     "Shoulders", 3, 10, 15, 7, 60),
                            Pe("curl",     "Barbell Curl",  "Arms",      3, 8, 12, 8, 60),
                        }
                    },
                    new PlanDay
                    {
                        DayIndex = 2, Label = "Legs",
                        Exercises = new List<PlannedExercise>
                        {
                            Pe("squat",    "Back Squat",         "Legs", 4, 4, 6, 8),
                            Pe("rdl",      "Romanian Deadlift",  "Back", 4, 4, 6, 8),
                            Pe("legpress", "Leg Press",          "Legs", 3, 8, 12, 8),
                            Pe("legcurl",  "Leg Curl",           "Legs", 3, 10, 15, 8, 60),
                            Pe("calf",     "Standing Calf Raise","Legs", 3, 10, 15, 8, 60),
                        }
                    },
                }
            };
        }

        private static bool Has(IReadOnlyList<Violation> violations, string code)
            => violations.Any(v => v.Code == code);

        // ── Baseline sanity ────────────────────────────────────────────────────

        [Fact]
        public void Baseline_valid_plan_has_zero_errors()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.DoesNotContain(v, x => x.Severity == Severity.Error);
        }

        // ── UNKNOWN_EXERCISE ───────────────────────────────────────────────────

        [Fact]
        public void UnknownExercise_not_triggered_when_all_ids_known()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.UnknownExercise));
        }

        [Fact]
        public void UnknownExercise_triggered_when_id_missing_from_library()
        {
            var plan = MakeValidPlan();
            plan.Days[0].Exercises[0].ExerciseId = "does-not-exist";
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.UnknownExercise));
        }

        // ── EQUIPMENT_UNAVAILABLE ──────────────────────────────────────────────

        [Fact]
        public void EquipmentUnavailable_not_triggered_when_overlap_exists()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.EquipmentUnavailable));
        }

        [Fact]
        public void EquipmentUnavailable_triggered_when_no_overlap()
        {
            var profile = MakeProfile();
            profile.AvailableEquipment = new List<EquipmentType> { EquipmentType.Bodyweight };
            var v = PlanValidator.Validate(MakeValidPlan(), profile, MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.EquipmentUnavailable));
        }

        // ── STRESS_TAG_CONFLICT ────────────────────────────────────────────────

        [Fact]
        public void StressTagConflict_not_triggered_when_no_tags_avoided()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.StressTagConflict));
        }

        [Fact]
        public void StressTagConflict_triggered_when_exercise_loads_avoided_region()
        {
            var profile = MakeProfile();
            profile.AvoidStressTags = new List<string> { "knee" };
            var v = PlanValidator.Validate(MakeValidPlan(), profile, MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.StressTagConflict));
        }

        // ── EXCLUDED_EXERCISE ──────────────────────────────────────────────────

        [Fact]
        public void ExcludedExercise_not_triggered_when_exclusions_empty()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.ExcludedExercise));
        }

        [Fact]
        public void ExcludedExercise_triggered_when_plan_uses_excluded_id()
        {
            var profile = MakeProfile();
            profile.ExcludedExerciseIds = new List<string> { "bench" };
            var v = PlanValidator.Validate(MakeValidPlan(), profile, MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.ExcludedExercise));
        }

        // ── LEVEL_TOO_HIGH ─────────────────────────────────────────────────────

        [Fact]
        public void LevelTooHigh_not_triggered_when_min_level_matches_athlete()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.LevelTooHigh));
        }

        [Fact]
        public void LevelTooHigh_triggered_when_exercise_requires_higher_level()
        {
            var plan = MakeValidPlan();
            // Replace one exercise with the Advanced-only variant.
            plan.Days[2].Exercises[1] = Pe("advanced_only", "Deficit Deadlift", "Back", 4, 4, 6, 8);
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.LevelTooHigh));
        }

        // ── VOLUME_BELOW_MEV ───────────────────────────────────────────────────

        [Fact]
        public void VolumeBelowMev_not_triggered_when_volume_in_range()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.VolumeBelowMev));
        }

        [Fact]
        public void VolumeBelowMev_triggered_when_chest_volume_cut_below_mev()
        {
            var plan = MakeValidPlan();
            // Cut all three chest exercises down to 1 set each (= 3 primary + 0 secondary < 8 MEV).
            foreach (var ex in plan.Days[0].Exercises.Where(e => e.MuscleGroup == "Chest"))
                ex.Sets = 1;
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.VolumeBelowMev));
        }

        // ── VOLUME_ABOVE_MRV ───────────────────────────────────────────────────

        [Fact]
        public void VolumeAboveMrv_not_triggered_when_volume_in_range()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.VolumeAboveMrv));
        }

        [Fact]
        public void VolumeAboveMrv_triggered_when_leg_volume_crammed_above_mrv()
        {
            var plan = MakeValidPlan();
            foreach (var ex in plan.Days[2].Exercises.Where(e => e.MuscleGroup == "Legs"))
                ex.Sets = 10;
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.VolumeAboveMrv));
        }

        // ── SESSION_TOO_LONG ───────────────────────────────────────────────────

        [Fact]
        public void SessionTooLong_not_triggered_at_default_session_limit()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.SessionTooLong));
        }

        [Fact]
        public void SessionTooLong_triggered_when_profile_limit_very_short()
        {
            var profile = MakeProfile();
            profile.SessionMinutes = 30;
            var v = PlanValidator.Validate(MakeValidPlan(), profile, MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.SessionTooLong));
        }

        // ── DAY_COUNT_MISMATCH ─────────────────────────────────────────────────

        [Fact]
        public void DayCountMismatch_not_triggered_when_days_equal_profile()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.DayCountMismatch));
        }

        [Fact]
        public void DayCountMismatch_triggered_when_plan_has_extra_day()
        {
            var plan = MakeValidPlan();
            plan.Days.Add(new PlanDay { DayIndex = 3, Label = "Extra", Exercises = new() });
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.DayCountMismatch));
        }

        // ── FREQUENCY_TOO_LOW (hypertrophy only) ───────────────────────────────

        [Fact]
        public void FrequencyTooLow_not_triggered_when_goal_is_strength()
        {
            // Baseline plan has Chest/Legs on single days, but goal=Strength.
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.FrequencyTooLow));
        }

        [Fact]
        public void FrequencyTooLow_triggered_when_goal_is_hypertrophy_and_muscle_hit_once()
        {
            var plan = MakeValidPlan();
            plan.Goal = Goal.Hypertrophy;
            var profile = MakeProfile();
            profile.Goal = Goal.Hypertrophy;
            var v = PlanValidator.Validate(plan, profile, MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.FrequencyTooLow));
        }

        // ── INVALID_PRESCRIPTION ───────────────────────────────────────────────

        [Fact]
        public void InvalidPrescription_not_triggered_for_sensible_prescriptions()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.InvalidPrescription));
        }

        [Fact]
        public void InvalidPrescription_triggered_when_rep_min_exceeds_rep_max()
        {
            var plan = MakeValidPlan();
            plan.Days[0].Exercises[0].RepMin = 12;
            plan.Days[0].Exercises[0].RepMax = 6;
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.InvalidPrescription));
        }

        // ── PATTERN_IMBALANCE ──────────────────────────────────────────────────

        [Fact]
        public void PatternImbalance_not_triggered_at_balanced_push_pull_ratio()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.PatternImbalance));
        }

        [Fact]
        public void PatternImbalance_triggered_when_pull_volume_vastly_outweighs_push()
        {
            var plan = MakeValidPlan();
            // Zero out all push-pattern sets. Pull-only plan triggers imbalance.
            foreach (var day in plan.Days)
                foreach (var ex in day.Exercises)
                    if (ex.MuscleGroup == "Chest" || ex.ExerciseId == "ohp")
                        ex.Sets = 1;
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.PatternImbalance));
        }

        // ── DUPLICATE_IN_DAY ───────────────────────────────────────────────────

        [Fact]
        public void DuplicateInDay_not_triggered_when_each_exercise_appears_once()
        {
            var v = PlanValidator.Validate(MakeValidPlan(), MakeProfile(), MakeExerciseLibrary());
            Assert.False(Has(v, ViolationCode.DuplicateInDay));
        }

        [Fact]
        public void DuplicateInDay_triggered_when_same_exercise_appears_twice_in_one_day()
        {
            var plan = MakeValidPlan();
            plan.Days[0].Exercises.Add(Pe("bench", "Bench Press", "Chest", 2, 4, 6, 8));
            var v = PlanValidator.Validate(plan, MakeProfile(), MakeExerciseLibrary());
            Assert.True(Has(v, ViolationCode.DuplicateInDay));
        }
    }
}
