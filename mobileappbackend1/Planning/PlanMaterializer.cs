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
        public static List<Workout> Materialize(
            TrainingPlan plan, DateTime startDate, string trainerId)
        {
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
