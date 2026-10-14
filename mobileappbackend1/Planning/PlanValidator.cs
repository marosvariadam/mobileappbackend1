using mobileappbackend1.Models;

namespace mobileappbackend1.Planning
{
    // Pure logic: no Mongo, no HTTP. Caller is responsible for pre-fetching
    // the exercise dictionary (keyed by Exercise.Id) before calling Validate.
    public static class PlanValidator
    {
        // Assumed per-set work time for session-length math. Rest comes from
        // PlannedExercise.RestSeconds.
        private const int WorkSecondsPerSet = 45;

        // Secondary muscles count fractionally toward volume (RP convention).
        private const double SecondaryVolumeWeight = 0.5;

        // Push:pull weekly set ratio must stay inside this band.
        private const double PushPullMinRatio = 0.67;
        private const double PushPullMaxRatio = 1.5;

        public static IReadOnlyList<Violation> Validate(
            TrainingPlan plan,
            AthleteProfile profile,
            IReadOnlyDictionary<string, Exercise> exercises)
        {
            var violations = new List<Violation>();

            CheckDayCountMatchesProfile(plan, profile, violations);
            CheckDuplicatesInDay(plan, violations);
            CheckInvalidPrescription(plan, violations);
            CheckExerciseLevelConstraints(plan, profile, exercises, violations);
            CheckSessionLength(plan, profile, violations);
            CheckVolumeLandmarks(plan, profile, exercises, violations);
            CheckFrequency(plan, profile, exercises, violations);
            CheckPushPullBalance(plan, exercises, violations);

            return violations;
        }

        // ── Rules ──────────────────────────────────────────────────────────────

        private static void CheckDayCountMatchesProfile(
            TrainingPlan plan, AthleteProfile profile, List<Violation> sink)
        {
            if (plan.Days.Count != profile.DaysPerWeek)
            {
                sink.Add(new Violation
                {
                    Code = ViolationCode.DayCountMismatch,
                    Severity = Severity.Error,
                    Message = $"Plan has {plan.Days.Count} day(s) but profile requests {profile.DaysPerWeek}.",
                });
            }
        }

        private static void CheckDuplicatesInDay(TrainingPlan plan, List<Violation> sink)
        {
            foreach (var day in plan.Days)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ex in day.Exercises)
                {
                    if (string.IsNullOrEmpty(ex.ExerciseId)) continue;
                    if (!seen.Add(ex.ExerciseId))
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.DuplicateInDay,
                            Severity = Severity.Error,
                            Message = $"'{ex.Name}' appears more than once on day {day.DayIndex}.",
                            DayIndex = day.DayIndex,
                            ExerciseId = ex.ExerciseId,
                        });
                    }
                }
            }
        }

        private static void CheckInvalidPrescription(TrainingPlan plan, List<Violation> sink)
        {
            foreach (var day in plan.Days)
            {
                foreach (var ex in day.Exercises)
                {
                    string? problem = null;
                    if (ex.RepMin > ex.RepMax) problem = $"RepMin ({ex.RepMin}) > RepMax ({ex.RepMax}).";
                    else if (ex.Sets < 1 || ex.Sets > 10) problem = $"Sets must be in 1..10 (got {ex.Sets}).";
                    else if (ex.TargetRpe < 5 || ex.TargetRpe > 10) problem = $"TargetRpe must be in 5..10 (got {ex.TargetRpe}).";

                    if (problem != null)
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.InvalidPrescription,
                            Severity = Severity.Error,
                            Message = $"'{ex.Name}' on day {day.DayIndex}: {problem}",
                            DayIndex = day.DayIndex,
                            ExerciseId = ex.ExerciseId,
                        });
                    }
                }
            }
        }

        // UNKNOWN_EXERCISE, EQUIPMENT_UNAVAILABLE, STRESS_TAG_CONFLICT,
        // EXCLUDED_EXERCISE, and LEVEL_TOO_HIGH share a single pass to avoid
        // re-looking-up each exercise multiple times.
        private static void CheckExerciseLevelConstraints(
            TrainingPlan plan,
            AthleteProfile profile,
            IReadOnlyDictionary<string, Exercise> exercises,
            List<Violation> sink)
        {
            var available = new HashSet<EquipmentType>(profile.AvailableEquipment);
            var avoidTags = new HashSet<string>(profile.AvoidStressTags, StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(profile.ExcludedExerciseIds);

            foreach (var day in plan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    if (string.IsNullOrEmpty(pe.ExerciseId)) continue;

                    if (excluded.Contains(pe.ExerciseId))
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.ExcludedExercise,
                            Severity = Severity.Error,
                            Message = $"'{pe.Name}' is on the athlete's exclusion list.",
                            DayIndex = day.DayIndex,
                            ExerciseId = pe.ExerciseId,
                        });
                    }

                    if (!exercises.TryGetValue(pe.ExerciseId, out var ex))
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.UnknownExercise,
                            Severity = Severity.Error,
                            Message = $"Unknown exercise id '{pe.ExerciseId}' on day {day.DayIndex}.",
                            DayIndex = day.DayIndex,
                            ExerciseId = pe.ExerciseId,
                        });
                        continue;
                    }

                    if (available.Count > 0 && ex.Equipment.Count > 0
                        && !ex.Equipment.Any(available.Contains))
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.EquipmentUnavailable,
                            Severity = Severity.Error,
                            Message = $"'{ex.Name}' needs equipment the athlete doesn't have ({string.Join(", ", ex.Equipment)}).",
                            DayIndex = day.DayIndex,
                            ExerciseId = pe.ExerciseId,
                        });
                    }

                    if (avoidTags.Count > 0 && ex.StressTags.Any(t => avoidTags.Contains(t)))
                    {
                        var hit = ex.StressTags.First(t => avoidTags.Contains(t));
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.StressTagConflict,
                            Severity = Severity.Error,
                            Message = $"'{ex.Name}' loads an avoided region ({hit}).",
                            DayIndex = day.DayIndex,
                            ExerciseId = pe.ExerciseId,
                        });
                    }

                    if ((int)ex.MinLevel > (int)profile.ExperienceLevel)
                    {
                        sink.Add(new Violation
                        {
                            Code = ViolationCode.LevelTooHigh,
                            Severity = Severity.Error,
                            Message = $"'{ex.Name}' requires {ex.MinLevel}, athlete is {profile.ExperienceLevel}.",
                            DayIndex = day.DayIndex,
                            ExerciseId = pe.ExerciseId,
                        });
                    }
                }
            }
        }

        private static void CheckSessionLength(
            TrainingPlan plan, AthleteProfile profile, List<Violation> sink)
        {
            var limitSeconds = profile.SessionMinutes * 60;
            foreach (var day in plan.Days)
            {
                var seconds = day.Exercises.Sum(e => e.Sets * (WorkSecondsPerSet + e.RestSeconds));
                if (seconds > limitSeconds)
                {
                    sink.Add(new Violation
                    {
                        Code = ViolationCode.SessionTooLong,
                        Severity = Severity.Error,
                        Message = $"Day {day.DayIndex} estimated {seconds / 60} min, limit {profile.SessionMinutes} min.",
                        DayIndex = day.DayIndex,
                    });
                }
            }
        }

        private static void CheckVolumeLandmarks(
            TrainingPlan plan,
            AthleteProfile profile,
            IReadOnlyDictionary<string, Exercise> exercises,
            List<Violation> sink)
        {
            var weekly = WeeklySetsPerMuscleGroup(plan, exercises);
            foreach (var (muscle, sets) in weekly)
            {
                var range = VolumeLandmarks.For(muscle, profile.ExperienceLevel);
                if (sets < range.Mev)
                {
                    sink.Add(new Violation
                    {
                        Code = ViolationCode.VolumeBelowMev,
                        Severity = Severity.Error,
                        Message = $"{muscle} weekly volume {sets:0.#} sets < MEV {range.Mev}.",
                    });
                }
                else if (sets > range.Mrv)
                {
                    sink.Add(new Violation
                    {
                        Code = ViolationCode.VolumeAboveMrv,
                        Severity = Severity.Error,
                        Message = $"{muscle} weekly volume {sets:0.#} sets > MRV {range.Mrv}.",
                    });
                }
            }
        }

        private static void CheckFrequency(
            TrainingPlan plan,
            AthleteProfile profile,
            IReadOnlyDictionary<string, Exercise> exercises,
            List<Violation> sink)
        {
            if (profile.Goal != Goal.Hypertrophy) return;

            // Days a muscle group is loaded at all (primary OR secondary).
            var daysPerMuscle = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var day in plan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    if (!string.IsNullOrEmpty(pe.MuscleGroup))
                        Mark(pe.MuscleGroup, day.DayIndex);

                    if (!string.IsNullOrEmpty(pe.ExerciseId)
                        && exercises.TryGetValue(pe.ExerciseId, out var ex))
                    {
                        foreach (var sec in ex.SecondaryMuscleGroups)
                            Mark(sec, day.DayIndex);
                    }
                }
            }

            foreach (var (muscle, days) in daysPerMuscle)
            {
                if (days.Count < 2)
                {
                    sink.Add(new Violation
                    {
                        Code = ViolationCode.FrequencyTooLow,
                        Severity = Severity.Warning,
                        Message = $"{muscle} trained {days.Count} day(s)/week; hypertrophy benefits from 2+.",
                    });
                }
            }

            void Mark(string muscle, int dayIndex)
            {
                if (!daysPerMuscle.TryGetValue(muscle, out var set))
                {
                    set = new HashSet<int>();
                    daysPerMuscle[muscle] = set;
                }
                set.Add(dayIndex);
            }
        }

        private static void CheckPushPullBalance(
            TrainingPlan plan,
            IReadOnlyDictionary<string, Exercise> exercises,
            List<Violation> sink)
        {
            int push = 0, pull = 0;
            foreach (var day in plan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    if (string.IsNullOrEmpty(pe.ExerciseId)) continue;
                    if (!exercises.TryGetValue(pe.ExerciseId, out var ex)) continue;

                    switch (ex.MovementPattern)
                    {
                        case MovementPattern.HorizontalPush:
                        case MovementPattern.VerticalPush:
                            push += pe.Sets;
                            break;
                        case MovementPattern.HorizontalPull:
                        case MovementPattern.VerticalPull:
                            pull += pe.Sets;
                            break;
                    }
                }
            }

            // Both zero: nothing to compare. Only one side zero: definitively imbalanced.
            if (push == 0 && pull == 0) return;
            if (push == 0 || pull == 0)
            {
                sink.Add(new Violation
                {
                    Code = ViolationCode.PatternImbalance,
                    Severity = Severity.Warning,
                    Message = $"Push:Pull ratio imbalanced ({push}:{pull}).",
                });
                return;
            }

            double ratio = (double)push / pull;
            if (ratio < PushPullMinRatio || ratio > PushPullMaxRatio)
            {
                sink.Add(new Violation
                {
                    Code = ViolationCode.PatternImbalance,
                    Severity = Severity.Warning,
                    Message = $"Push:Pull ratio {ratio:0.00} outside [{PushPullMinRatio}, {PushPullMaxRatio}] ({push} push sets vs {pull} pull sets).",
                });
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        // Weekly working sets per muscle group across the whole microcycle.
        // Primary = 1 set per planned set; each entry in SecondaryMuscleGroups
        // of the mapped Exercise contributes 0.5. Entries without a known
        // ExerciseId still contribute to their primary (denormalized) muscle
        // group so partially-materialized plans don't drop volume on the floor.
        private static Dictionary<string, double> WeeklySetsPerMuscleGroup(
            TrainingPlan plan, IReadOnlyDictionary<string, Exercise> exercises)
        {
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (var day in plan.Days)
            {
                foreach (var pe in day.Exercises)
                {
                    if (!string.IsNullOrEmpty(pe.MuscleGroup))
                        Add(pe.MuscleGroup, pe.Sets);

                    if (!string.IsNullOrEmpty(pe.ExerciseId)
                        && exercises.TryGetValue(pe.ExerciseId, out var ex))
                    {
                        foreach (var sec in ex.SecondaryMuscleGroups)
                            Add(sec, pe.Sets * SecondaryVolumeWeight);
                    }
                }
            }

            return totals;

            void Add(string muscle, double amount)
            {
                totals.TryGetValue(muscle, out var current);
                totals[muscle] = current + amount;
            }
        }
    }
}
