using mobileappbackend1.ML;
using mobileappbackend1.Models;

namespace mobileappbackend1.Services
{
    /// <summary>Turns completed-workout history + training blocks into weekly feature vectors.</summary>
    public class FeatureEngineeringService
    {
        private readonly WorkoutService _workoutService;
        private readonly TrainingBlockService _blockService;
        private readonly UserService _userService;

        public FeatureEngineeringService(
            WorkoutService workoutService,
            TrainingBlockService blockService,
            UserService userService)
        {
            _workoutService = workoutService;
            _blockService = blockService;
            _userService = userService;
        }

        // Epley formula: upper bound of a true 1RM given a multi-rep set.
        // Returns weight when reps == 1, approaches weight * (1 + reps/30) higher.
        private static double Epley(double weightKg, int reps) =>
            reps <= 0 ? 0 : weightKg * (1.0 + reps / 30.0);

        // Monday of the ISO week containing dt, at 00:00 UTC.
        private static DateTime MondayOf(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            int diff = (int)utc.DayOfWeek - (int)DayOfWeek.Monday;
            if (diff < 0) diff += 7;
            return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc)
                .AddDays(-diff);
        }

        // The focus for a given week = the block covering its Monday, else "Full".
        private static string ResolveFocus(List<TrainingBlock> blocks, DateTime weekStart)
        {
            var block = blocks.FirstOrDefault(b =>
                b.StartDate.Date <= weekStart.Date && b.EndDate.Date >= weekStart.Date);
            return block?.Focus ?? "Full";
        }

        /// <summary>Build per-week aggregate features for one (athlete, exercise) pair.</summary>
        public async Task<List<WeeklyFeatureVector>> BuildWeeklyVectorsAsync(
            string athleteId, string exerciseName, DateTime from, DateTime to)
        {
            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null) return new List<WeeklyFeatureVector>();

            var workouts = await _workoutService.GetCompletedByAthleteAndDateRangeAsync(athleteId, from, to);
            var blocks = await _blockService.GetByAthleteDateRangeAsync(athleteId, from, to);

            // Flatten matching completed exercises with their completion date.
            var logged = workouts
                .SelectMany(w => w.Exercises
                    .Where(e => e.IsCompleted
                                && e.Name.Equals(exerciseName, StringComparison.OrdinalIgnoreCase)
                                && w.CompletedAt.HasValue)
                    .Select(e => new
                    {
                        Date        = w.CompletedAt!.Value,
                        e.ActualSets,
                        e.ActualRepetitions,
                        e.ActualWeightKg,
                        e.Rpe,
                        e.MuscleGroup
                    }))
                .ToList();

            if (logged.Count == 0) return new List<WeeklyFeatureVector>();

            // Infer the exercise's muscle group from any row that carries it.
            var muscleGroup = logged
                .Select(l => l.MuscleGroup)
                .FirstOrDefault(mg => !string.IsNullOrWhiteSpace(mg));

            // Training-age anchor: explicit TrainingStartedAt, else CreatedAt.
            var trainingStart = athlete.TrainingStartedAt ?? athlete.CreatedAt;
            var bodyweight = athlete.WeightKg ?? 75.0;  // neutral default when missing

            var grouped = logged
                .GroupBy(l => MondayOf(l.Date))
                .OrderBy(g => g.Key)
                .ToList();

            var vectors = new List<WeeklyFeatureVector>();
            foreach (var group in grouped)
            {
                double volume = 0;
                double bestEpley = 0;
                int setCount = 0;
                int totalReps = 0;
                var rpeValues = new List<int>();

                foreach (var row in group)
                {
                    int sets = row.ActualSets ?? 0;
                    int reps = row.ActualRepetitions ?? 0;
                    double w = row.ActualWeightKg ?? 0;

                    volume    += sets * reps * w;
                    setCount  += sets;
                    totalReps += sets * reps;

                    var epley = Epley(w, reps);
                    if (epley > bestEpley) bestEpley = epley;

                    if (row.Rpe.HasValue) rpeValues.Add(row.Rpe.Value);
                }

                var weekStart = group.Key;
                var focus = ResolveFocus(blocks, weekStart);
                var overlap = MuscleOverlap.GetScore(focus, muscleGroup);
                var ageWeeks = Math.Max(0, (int)Math.Floor((weekStart - trainingStart).TotalDays / 7.0));

                vectors.Add(new WeeklyFeatureVector
                {
                    WeekStart        = weekStart,
                    AthleteId        = athleteId,
                    ExerciseName     = exerciseName,
                    MuscleGroup      = muscleGroup,
                    Focus            = focus,
                    VolumeKg         = volume,
                    Est1Rm           = bestEpley,
                    AvgRpe           = rpeValues.Count > 0 ? rpeValues.Average() : null,
                    SetCount         = setCount,
                    TotalReps        = totalReps,
                    OverlapScore     = overlap,
                    BodyweightKg     = bodyweight,
                    TrainingAgeWeeks = ageWeeks,
                    IsBeginner       = ageWeeks < 52,
                });
            }

            // Fill lagged features once the ordered list is built.
            for (int i = 1; i < vectors.Count; i++)
            {
                vectors[i].PrevWeekEst1Rm   = vectors[i - 1].Est1Rm;
                vectors[i].PrevWeekVolumeKg = vectors[i - 1].VolumeKg;
            }

            return vectors;
        }

        /// <summary>Build labeled rows across every athlete and every exercise they've logged.</summary>
        public async Task<List<LabeledTrainingRow>> BuildAllLabeledRowsAsync(DateTime from, DateTime to)
        {
            var athletes = await _userService.GetAllAthletesAsync();
            var all = new List<LabeledTrainingRow>();

            foreach (var athlete in athletes)
            {
                if (string.IsNullOrEmpty(athlete.Id)) continue;

                var workouts = await _workoutService.GetCompletedByAthleteAndDateRangeAsync(
                    athlete.Id, from, to);

                var distinctExercises = workouts
                    .SelectMany(w => w.Exercises)
                    .Where(e => e.IsCompleted)
                    .Select(e => e.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var exerciseName in distinctExercises)
                {
                    var rows = await BuildLabeledRowsAsync(athlete.Id, exerciseName, from, to);
                    all.AddRange(rows);
                }
            }

            return all;
        }

        /// <summary>Pair each week's features with the next week's 1RM delta as the label.</summary>
        public async Task<List<LabeledTrainingRow>> BuildLabeledRowsAsync(
            string athleteId, string exerciseName, DateTime from, DateTime to)
        {
            var vectors = await BuildWeeklyVectorsAsync(athleteId, exerciseName, from, to);
            var rows = new List<LabeledTrainingRow>(Math.Max(0, vectors.Count - 1));

            for (int i = 0; i < vectors.Count - 1; i++)
            {
                var current = vectors[i];
                var next    = vectors[i + 1];

                // Skip if the gap is larger than one ISO week (layoff / injury).
                if ((next.WeekStart - current.WeekStart).TotalDays > 7.5) continue;

                rows.Add(new LabeledTrainingRow
                {
                    Features            = current,
                    NextWeekEst1RmDelta = next.Est1Rm - current.Est1Rm,
                });
            }

            return rows;
        }
    }
}
