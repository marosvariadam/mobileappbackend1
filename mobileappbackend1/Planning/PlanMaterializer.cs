using mobileappbackend1.Models;

namespace mobileappbackend1.Planning
{
    // Pure logic: expand an accepted TrainingPlan into concrete Workout
    // documents for a given start date. Day DayIndex is treated as an offset
    // in days from the start date within one week; the plan is repeated
    // plan.Weeks times. Target weights are left at 0 - AutoregulationService
    // (Phase 7) fills them in from logged history.
    public static class PlanMaterializer
    {
        // Hard cap on the fan-out. 7 days * 52 weeks = 364 is the natural
        // ceiling (one plan can't book past a year or more than daily);
        // anything above this is almost certainly bad input (a 10000-week
        // plan, a 100-day microcycle) and we refuse to insert it rather than
        // flood Mongo with ghost workouts.
        public const int MaxWorkoutsPerMaterialization = 400;
        public const int MaxDaysPerWeek = 7;
        public const int MaxDayIndex = 6;
        public const int MaxWeeks = 52;

        public static List<Workout> Materialize(
            TrainingPlan plan, DateTime startDate, string trainerId)
        {
            if (plan.Weeks < 1 || plan.Weeks > MaxWeeks)
                throw new ArgumentOutOfRangeException(
                    nameof(plan), $"plan.Weeks must be in 1..{MaxWeeks} (got {plan.Weeks}).");
            if (plan.Days.Count == 0 || plan.Days.Count > MaxDaysPerWeek)
                throw new ArgumentOutOfRangeException(
                    nameof(plan), $"plan.Days.Count must be in 1..{MaxDaysPerWeek} (got {plan.Days.Count}).");
            if (plan.Days.Any(d => d.DayIndex < 0 || d.DayIndex > MaxDayIndex))
                throw new ArgumentOutOfRangeException(
                    nameof(plan), $"every PlanDay.DayIndex must be in 0..{MaxDayIndex}.");
            if (plan.Weeks * plan.Days.Count > MaxWorkoutsPerMaterialization)
                throw new ArgumentOutOfRangeException(
                    nameof(plan),
                    $"plan.Weeks * plan.Days.Count ({plan.Weeks * plan.Days.Count}) exceeds the "
                    + $"materialization cap of {MaxWorkoutsPerMaterialization}.");

            var workouts = new List<Workout>(plan.Weeks * plan.Days.Count);
            for (int week = 0; week < plan.Weeks; week++)
            {
                foreach (var day in plan.Days)
                {
                    var scheduled = startDate
                        .ToUniversalTime()
                        .AddDays(week * 7 + day.DayIndex);

                    workouts.Add(new Workout
                    {
                        TrainerId     = trainerId,
                        AthleteId     = plan.AthleteId,
                        Title         = $"{plan.Title} - W{week + 1} {day.Label}",
                        TrainerNotes  = null,
                        Difficulty    = DifficultyLevel.Moderate,
                        Status        = WorkoutStatus.Planned,
                        ScheduledDate = scheduled,
                        Exercises     = day.Exercises.Select(e => new WorkoutExercise
                        {
                            ExerciseId        = e.ExerciseId,
                            Name              = e.Name,
                            MuscleGroup       = e.MuscleGroup,
                            Sets              = e.Sets,
                            TargetRepetitions = Math.Max(1, (e.RepMin + e.RepMax) / 2),
                            TargetWeightKg    = 0,
                            TrainerNotes      = e.Notes,
                        }).ToList(),
                    });
                }
            }
            return workouts;
        }
    }
}
