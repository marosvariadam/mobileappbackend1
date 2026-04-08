using mobileappbackend1.Models;
using mobileappbackend1.Models.Prediction;

namespace mobileappbackend1.Services.ML
{
    /// <summary>
    /// Aggregates raw workout logs into weekly <see cref="ExerciseFeatureVector"/> rows
    /// suitable for ML.NET training and inference.
    /// </summary>
    public class FeatureEngineeringService
    {
        private readonly WorkoutService  _workoutService;
        private readonly ExerciseService _exerciseService;
        private readonly UserService     _userService;

        public FeatureEngineeringService(
            WorkoutService  workoutService,
            ExerciseService exerciseService,
            UserService     userService)
        {
            _workoutService  = workoutService;
            _exerciseService = exerciseService;
            _userService     = userService;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns weekly feature vectors for <paramref name="athleteId"/> and the target
        /// <paramref name="exerciseName"/>, sorted oldest-first.
        ///
        /// The LAST row in the list is the "current" week — it has no
        /// <see cref="ExerciseFeatureVector.NextWeekOneRmDelta"/> label and is used as the
        /// seed for the iterative roll-forward prediction, not for training.
        ///
        /// All earlier rows have valid labels and can be used directly as training data.
        /// </summary>
        public async Task<List<ExerciseFeatureVector>> GetWeeklyVectorsAsync(
            string athleteId,
            string exerciseName,
            int    maxWeeks = 104)
        {
            var workouts  = await _workoutService.GetCompletedForMlAsync(athleteId, maxWeeks);
            var user      = await _userService.GetByIdAsync(athleteId);
            var bodyweight = (float)(user?.WeightKg ?? 75.0);

            // Batch-resolve muscle groups for all exercises that have an ExerciseId
            var allExerciseIds = workouts
                .SelectMany(w => w.Exercises)
                .Where(e  => !string.IsNullOrEmpty(e.ExerciseId))
                .Select(e => e.ExerciseId!)
                .Distinct();

            var exerciseDb = await _exerciseService.GetByIdsAsync(allExerciseIds);

            // Group workouts into ISO weeks (Monday = start)
            var byWeek = workouts
                .Where(w => w.CompletedAt.HasValue)
                .GroupBy(w => GetWeekStart(w.CompletedAt!.Value))
                .OrderBy(g => g.Key)
                .ToList();

            if (byWeek.Count == 0)
                return new List<ExerciseFeatureVector>();

            var firstWorkoutDate = byWeek.First().Key;
            var normTarget       = MuscleOverlapMatrix.NormaliseExerciseName(exerciseName);

            // ── Build per-week rows ────────────────────────────────────────────
            var rows = new List<ExerciseFeatureVector>();

            foreach (var weekGroup in byWeek)
            {
                var weekStart    = weekGroup.Key;
                var weekWorkouts = weekGroup.ToList();

                // ── Target-exercise stats ──────────────────────────────────────
                var targetLogs = weekWorkouts
                    .SelectMany(w => w.Exercises)
                    .Where(e => IsTargetExercise(e.Name, normTarget) &&
                                e.ActualWeightKg.HasValue && e.ActualWeightKg > 0 &&
                                e.ActualRepetitions.HasValue && e.ActualRepetitions > 0 &&
                                e.ActualSets.HasValue && e.ActualSets > 0)
                    .ToList();

                if (targetLogs.Count == 0) continue; // week had no relevant exercise data

                var epleyOneRm = targetLogs
                    .Select(e => EpleyOneRm((float)e.ActualWeightKg!.Value,
                                            e.ActualRepetitions!.Value))
                    .Max();

                // Guard: cap at bodyweight × 5 to filter data-entry errors
                epleyOneRm = MathF.Min(epleyOneRm, bodyweight * 5f);

                var targetVolume = targetLogs
                    .Sum(e => e.ActualSets!.Value * e.ActualRepetitions!.Value * (float)e.ActualWeightKg!.Value);

                var sessionCount = weekWorkouts
                    .Count(w => w.Exercises.Any(e => IsTargetExercise(e.Name, normTarget)));

                // ── Overlap score ──────────────────────────────────────────────
                float totalWeekVolume = 0f;
                float weightedOverlap = 0f;

                foreach (var workout in weekWorkouts)
                {
                    foreach (var ex in workout.Exercises)
                    {
                        if (ex.ActualWeightKg is null or <= 0) continue;
                        if (ex.ActualSets is null or <= 0)     continue;
                        if (ex.ActualRepetitions is null or <= 0) continue;

                        var exVolume = ex.ActualSets.Value * ex.ActualRepetitions.Value * (float)ex.ActualWeightKg.Value;

                        // Resolve muscle group: prefer Exercise DB entry, fall back to empty
                        var muscleGroup = string.Empty;
                        if (!string.IsNullOrEmpty(ex.ExerciseId) &&
                            exerciseDb.TryGetValue(ex.ExerciseId, out var dbEx))
                        {
                            muscleGroup = dbEx.MuscleGroup;
                        }

                        var coeff = string.IsNullOrEmpty(muscleGroup)
                            ? MuscleOverlapMatrix.GetCoefficient(exerciseName, ex.Name) // fallback: match by exercise name
                            : MuscleOverlapMatrix.GetCoefficient(exerciseName, muscleGroup);

                        totalWeekVolume += exVolume;
                        weightedOverlap += exVolume * coeff;
                    }
                }

                var overlapScore = totalWeekVolume > 0f ? weightedOverlap / totalWeekVolume : 0f;

                // ── Difficulty average ─────────────────────────────────────────
                var avgDifficulty = weekWorkouts
                    .Select(w => (float)w.Difficulty)   // enum int value 1–4
                    .Average();

                // ── Training age ───────────────────────────────────────────────
                var trainingAgeWeeks = (float)(weekStart - firstWorkoutDate).TotalDays / 7f;

                rows.Add(new ExerciseFeatureVector
                {
                    WeekStart          = weekStart,
                    CurrentOneRmKg     = epleyOneRm,
                    WeeklyVolumeKg     = targetVolume,
                    SessionCount       = sessionCount,
                    OverlapScore       = overlapScore,
                    TrainingAgeWeeks   = trainingAgeWeeks,
                    BodyweightKg       = bodyweight,
                    AvgDifficultyScore = avgDifficulty,
                    // Lag features filled in the second pass below
                });
            }

            // ── Second pass: lag features + trend slope + labels ──────────────
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].PrevWeekOneRmKg  = i > 0 ? rows[i - 1].CurrentOneRmKg  : 0f;
                rows[i].PrevWeekVolumeKg = i > 0 ? rows[i - 1].WeeklyVolumeKg  : 0f;

                // 4-week window for trend slope (take up to 4 previous rows)
                var windowStart = Math.Max(0, i - 3);
                var window = rows.Skip(windowStart).Take(i - windowStart + 1)
                                 .Select(r => r.CurrentOneRmKg).ToArray();
                rows[i].FourWeekTrendSlope = SyntheticDataService.LinearSlope(window);

                // Label: delta to NEXT week's 1RM (last row has no label)
                rows[i].NextWeekOneRmDelta = i < rows.Count - 1
                    ? rows[i + 1].CurrentOneRmKg - rows[i].CurrentOneRmKg
                    : 0f;
            }

            return rows;
        }

        /// <summary>
        /// Returns the muscle-focus distribution for the last <paramref name="lookbackWeeks"/> weeks,
        /// expressed as fraction of total volume per high-level category.
        /// </summary>
        public async Task<Dictionary<string, float>> GetMuscleFocusDistributionAsync(
            string athleteId,
            int    lookbackWeeks = 4)
        {
            var workouts   = await _workoutService.GetCompletedForMlAsync(athleteId, lookbackWeeks);
            var exerciseIds = workouts
                .SelectMany(w => w.Exercises)
                .Where(e => !string.IsNullOrEmpty(e.ExerciseId))
                .Select(e => e.ExerciseId!)
                .Distinct();
            var exerciseDb = await _exerciseService.GetByIdsAsync(exerciseIds);

            var categoryVolume = new Dictionary<string, float>();
            float total = 0f;

            foreach (var workout in workouts)
            foreach (var ex in workout.Exercises)
            {
                if (ex.ActualWeightKg is null or <= 0) continue;
                if (ex.ActualSets is null or <= 0)     continue;
                if (ex.ActualRepetitions is null or <= 0) continue;

                var vol = ex.ActualSets.Value * ex.ActualRepetitions.Value * (float)ex.ActualWeightKg.Value;
                total += vol;

                var muscleGroup = !string.IsNullOrEmpty(ex.ExerciseId) &&
                                  exerciseDb.TryGetValue(ex.ExerciseId, out var dbEx)
                    ? dbEx.MuscleGroup
                    : string.Empty;

                var category = string.IsNullOrEmpty(muscleGroup)
                    ? "other"
                    : MuscleOverlapMatrix.GetCategory(muscleGroup);

                categoryVolume.TryGetValue(category, out var existing);
                categoryVolume[category] = existing + vol;
            }

            if (total <= 0f) return categoryVolume;

            foreach (var key in categoryVolume.Keys.ToList())
                categoryVolume[key] = categoryVolume[key] / total;

            return categoryVolume;
        }

        /// <summary>
        /// Returns distinct normalised exercise names for which the athlete has at least
        /// <paramref name="minWeeks"/> weeks of logged data.
        /// </summary>
        public async Task<List<(string ExerciseName, int WeekCount, float LatestOneRm)>>
            GetTrainableExercisesAsync(string athleteId, int minWeeks = 4)
        {
            var workouts = await _workoutService.GetCompletedForMlAsync(athleteId);

            var exerciseWeeks = workouts
                .Where(w => w.CompletedAt.HasValue)
                .SelectMany(w => w.Exercises.Select(e => (
                    Name: MuscleOverlapMatrix.NormaliseExerciseName(e.Name),
                    WeekStart: GetWeekStart(w.CompletedAt!.Value),
                    Weight: e.ActualWeightKg,
                    Reps: e.ActualRepetitions)))
                .Where(x => x.Weight is > 0 && x.Reps is > 0)
                .GroupBy(x => x.Name)
                .Select(g =>
                {
                    var weeks      = g.Select(x => x.WeekStart).Distinct().Count();
                    var latestOneRm = g
                        .OrderByDescending(x => x.WeekStart)
                        .Take(5)
                        .Select(x => EpleyOneRm((float)x.Weight!.Value, x.Reps!.Value))
                        .DefaultIfEmpty(0f)
                        .Max();
                    return (g.Key, weeks, latestOneRm);
                })
                .Where(x => x.Item2 >= minWeeks)
                .OrderByDescending(x => x.Item2)
                .ToList();

            return exerciseWeeks;
        }

        // ── Static helpers ────────────────────────────────────────────────────

        /// <summary>Epley formula: weight × (1 + reps / 30). Capped at reps ≤ 30.</summary>
        public static float EpleyOneRm(float weightKg, int reps)
        {
            if (reps <= 0 || weightKg <= 0) return 0f;
            if (reps == 1) return weightKg;
            var clampedReps = Math.Min(reps, 30);
            return weightKg * (1f + clampedReps / 30f);
        }

        /// <summary>Returns the Monday of the ISO week containing <paramref name="date"/>.</summary>
        public static DateTime GetWeekStart(DateTime date)
        {
            var diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return date.Date.AddDays(-diff);
        }

        private static bool IsTargetExercise(string exerciseName, string normTarget)
        {
            var norm = MuscleOverlapMatrix.NormaliseExerciseName(exerciseName);
            return norm == normTarget || norm.Contains(normTarget) || normTarget.Contains(norm);
        }
    }
}
