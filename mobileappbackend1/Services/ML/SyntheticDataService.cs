using mobileappbackend1.Models.Prediction;

namespace mobileappbackend1.Services.ML
{
    /// <summary>
    /// Generates synthetic training curves for athletes who don't yet have enough real data (cold-start).
    /// Based on well-established novice/intermediate/advanced weekly strength-gain research.
    /// </summary>
    public class SyntheticDataService
    {
        // ── Progression rates (kg per week per phase) ─────────────────────────

        private static readonly Dictionary<string, (float Novice, float Intermediate, float Advanced)> _weeklyGainKg = new()
        {
            ["squat"]          = (2.50f, 1.00f, 0.25f),
            ["deadlift"]       = (3.00f, 1.50f, 0.40f),
            ["bench press"]    = (1.50f, 0.50f, 0.15f),
            ["overhead press"] = (1.00f, 0.35f, 0.10f),
            ["barbell row"]    = (1.50f, 0.60f, 0.15f),
        };

        // Bodyweight multipliers for estimating starting 1RM (novice baseline)
        private static readonly Dictionary<string, float> _bwMultiplier = new()
        {
            ["squat"]          = 1.00f,
            ["deadlift"]       = 1.25f,
            ["bench press"]    = 0.65f,
            ["overhead press"] = 0.40f,
            ["barbell row"]    = 0.65f,
        };

        private static readonly Random _rng = new(42); // fixed seed for reproducibility

        /// <summary>
        /// Generates a synthetic weekly feature-vector curve for the given exercise.
        /// <paramref name="trainingAgeWeeksAtStart"/> shifts which part of the progression curve to use.
        /// <paramref name="weeksToGenerate"/> is how many labelled rows to produce (last row has a label).
        /// </summary>
        public List<ExerciseFeatureVector> GenerateCurve(
            string exerciseName,
            float  bodyweightKg,
            float? startingOneRmKg,
            float  trainingAgeWeeksAtStart,
            int    weeksToGenerate = 52)
        {
            var norm = MuscleOverlapMatrix.NormaliseExerciseName(exerciseName);

            // Resolve alias
            if (!_weeklyGainKg.TryGetValue(norm, out var rates))
            {
                // Unknown exercise — use conservative intermediate rates
                rates = (1.00f, 0.40f, 0.10f);
            }

            var bwMult = _bwMultiplier.TryGetValue(norm, out var m) ? m : 0.75f;
            var oneRm  = startingOneRmKg ?? bodyweightKg * bwMult;

            var rows = new List<ExerciseFeatureVector>(weeksToGenerate + 1);
            float prevOneRm  = 0f;
            float prevVolume = 0f;
            var   window     = new Queue<float>(); // last 4 1RM values for trend

            for (int week = 0; week <= weeksToGenerate; week++)
            {
                var ageWeeks = trainingAgeWeeksAtStart + week;
                var gain     = WeeklyGain(rates, ageWeeks);

                // Add Gaussian noise ±15 % of gain to avoid perfectly linear data
                var noise = (float)(NextGaussian() * gain * 0.15);
                var nextOneRm = MathF.Max(oneRm + gain + noise, oneRm * 0.95f); // never drop > 5 %

                var volume = oneRm * 3 * 8; // synthetic: 3 sets × 8 reps at ~1RM
                var sessionCount = 2f;
                var difficulty   = ageWeeks < 12 ? 2.0f : ageWeeks < 52 ? 2.5f : 3.0f;

                window.Enqueue(oneRm);
                if (window.Count > 4) window.Dequeue();

                var row = new ExerciseFeatureVector
                {
                    WeekStart            = DateTime.UtcNow.AddDays(-(weeksToGenerate - week) * 7),
                    CurrentOneRmKg       = oneRm,
                    WeeklyVolumeKg       = volume,
                    SessionCount         = sessionCount,
                    OverlapScore         = 0.70f, // neutral assumption
                    TrainingAgeWeeks     = ageWeeks,
                    BodyweightKg         = bodyweightKg,
                    AvgDifficultyScore   = difficulty,
                    PrevWeekOneRmKg      = prevOneRm,
                    PrevWeekVolumeKg     = prevVolume,
                    FourWeekTrendSlope   = LinearSlope(window.ToArray()),
                    NextWeekOneRmDelta   = week < weeksToGenerate ? (nextOneRm - oneRm) : 0f,
                };

                rows.Add(row);

                prevOneRm  = oneRm;
                prevVolume = volume;
                oneRm      = nextOneRm;
            }

            // The last row has NextWeekOneRmDelta = 0 (no label) — callers should
            // exclude it from training but may use it as the prediction seed.
            return rows;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static float WeeklyGain(
            (float Novice, float Intermediate, float Advanced) rates,
            float ageWeeks)
        {
            if (ageWeeks < 12)  return rates.Novice;
            if (ageWeeks < 52)  return rates.Intermediate;
            return rates.Advanced;
        }

        /// <summary>Ordinary least-squares slope over a small array. Returns 0 for < 2 points.</summary>
        public static float LinearSlope(float[] values)
        {
            if (values.Length < 2) return 0f;
            int   n    = values.Length;
            float sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < n; i++)
            {
                sumX  += i;
                sumY  += values[i];
                sumXY += i * values[i];
                sumX2 += i * i;
            }
            float denom = n * sumX2 - sumX * sumX;
            return MathF.Abs(denom) < 1e-6f ? 0f : (n * sumXY - sumX * sumY) / denom;
        }

        private static double NextGaussian()
        {
            // Box–Muller transform
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = 1.0 - _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
