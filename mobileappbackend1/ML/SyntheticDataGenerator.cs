using Microsoft.Extensions.Options;
using mobileappbackend1.Settings;

namespace mobileappbackend1.ML
{
    /// <summary>
    /// Produces synthetic <see cref="LabeledTrainingRow"/> rows for cold-start
    /// training. The shape, timing, and tier transitions are grounded in
    /// published references where possible; constants without a clean published
    /// source are flagged inline as <c>// heuristic</c>.
    ///
    /// Sources (cited at point of use):
    ///   • calcffmi strength standards     https://calcffmi.com/strength-standards/
    ///   • Lyle McDonald muscle-gain table https://bodyrecomposition.com/page/137
    ///   • Coutinho et al. 2024 (deload)   https://pmc.ncbi.nlm.nih.gov/articles/PMC10948666/
    ///   • Frontiers RPE study             https://www.frontiersin.org/journals/physiology/articles/10.3389/fphys.2018.00247/full
    ///   • RP volume landmarks             https://rpstrength.com/blogs/articles/training-volume-landmarks-muscle-growth
    /// </summary>
    public class SyntheticDataGenerator
    {
        private readonly MLSettings _settings;

        public SyntheticDataGenerator(IOptions<MLSettings> settings)
        {
            _settings = settings.Value;
        }

        // ── Tier boundaries (calcffmi) ────────────────────────────────────────
        // Novice 0–6 mo, Beginner 6–18 mo, Intermediate 18–36 mo,
        // Advanced 3–5 yr, Elite 5+ yr.  In weeks:
        private const int NoviceEnd       = 26;
        private const int BeginnerEnd     = 78;
        private const int IntermediateEnd = 156;
        private const int AdvancedEnd     = 260;

        // ── Deload schedule (Coutinho 2024 PMC10948666) ───────────────────────
        // "6.4 ± 1.7 days every 5.6 ± 2.3 weeks". We treat a deload as one
        // synthetic week and sample the inter-deload interval per athlete.
        private const double DeloadIntervalMeanWeeks = 5.6;
        private const double DeloadIntervalStdWeeks  = 2.3;
        private const double DeloadGainMultiplier    = 0.10;  // near-zero growth on deload weeks

        // ── RPE distribution (Graham & Cleather 2018; MASS RPE/RIR guide) ─────
        // Working-set RPE clusters around 7–8.5 under autoregulation.
        private const double RpeMean   = 7.5;
        private const double RpeStdDev = 1.0;
        private const double RpeMinNormal = 5.0;
        private const double RpeMaxNormal = 9.5;
        private const double RpeMeanDeload = 5.5;  // heuristic — deloads are easier sets
        private const double RpeMinDeload  = 3.0;
        private const double RpeMaxDeload  = 7.0;

        // ── Bodyweight distribution ───────────────────────────────────────────
        // heuristic — adult-lifter rough population
        private const double BodyweightMeanKg = 75.0;
        private const double BodyweightStdKg  = 15.0;
        private const double BodyweightMinKg  = 50.0;
        private const double BodyweightMaxKg  = 130.0;

        // ── Multiplicative weekly noise ───────────────────────────────────────
        private const double WeeklyDeltaNoiseMean   = 1.0;
        private const double WeeklyDeltaNoiseStd    = 0.30;  // heuristic — keeps the shape but noisy

        // ── Tier mix ──────────────────────────────────────────────────────────
        // Realistic gym-population skew toward newer lifters; weighted to keep
        // ML coverage of all tiers. heuristic.
        private static readonly (int MinAgeWk, int MaxAgeWk, double Weight)[] TierMix =
        {
            (0,   NoviceEnd,                   0.25),
            (NoviceEnd,       BeginnerEnd,     0.30),
            (BeginnerEnd,     IntermediateEnd, 0.25),
            (IntermediateEnd, AdvancedEnd,     0.15),
            (AdvancedEnd,     520,             0.05),
        };

        // ── Canonical exercise set ────────────────────────────────────────────
        // Subset of the seeded exercise list. Skips bodyweight / time-based
        // movements (Pull-Up, Plank) since their "weight" is ambiguous.
        private static readonly (string Name, string MuscleGroup)[] CanonicalExercises =
        {
            ("Squat",                "Legs"),
            ("Romanian Deadlift",    "Legs"),
            ("Leg Press",            "Legs"),
            ("Deadlift",             "Back"),
            ("Barbell Row",          "Back"),
            ("Lat Pulldown",         "Back"),
            ("Bench Press",          "Chest"),
            ("Incline Bench Press",  "Chest"),
            ("Overhead Press",       "Shoulders"),
            ("Barbell Curl",         "Arms"),
            ("Tricep Pushdown",      "Arms"),
        };

        // ── Block focus rotation patterns ─────────────────────────────────────
        // heuristic — common training-split conventions
        private static readonly string[][] FocusPatterns =
        {
            new[] { "Push", "Pull", "Legs" },                 // PPL
            new[] { "Upper", "Lower" },                        // U/L
            new[] { "Full" },                                  // full body
            new[] { "Legs", "Push", "Pull", "Upper" },         // 4-week rotation
            new[] { "Push", "Pull", "Legs", "Push", "Pull", "Legs", "Full" },  // PPLx2 + recovery
        };

        /// <summary>
        /// Generate labeled rows for the configured number of athletes. The
        /// timeline is anchored at <paramref name="anchorDate"/> Mon-start UTC;
        /// each synthetic week increments by 7 days from there.
        /// </summary>
        public List<LabeledTrainingRow> Generate(
            int? athleteCount = null, int seed = 1, DateTime? anchorDate = null)
        {
            var rng = new Random(seed);
            var count = athleteCount ?? _settings.SyntheticAthleteCount;
            var anchor = anchorDate?.ToUniversalTime()
                         ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var rows = new List<LabeledTrainingRow>(capacity: count * 52 * 6);

            for (int i = 0; i < count; i++)
            {
                var weeks = GenerateAthleteWeeks(rng, i, anchor);
                AppendLabeledPairs(weeks, rows);
            }

            return rows;
        }

        // ── Per-athlete simulation ────────────────────────────────────────────

        private List<WeeklyFeatureVector> GenerateAthleteWeeks(
            Random rng, int athleteIndex, DateTime anchor)
        {
            var bodyweight = ClampedNormal(rng, BodyweightMeanKg, BodyweightStdKg, BodyweightMinKg, BodyweightMaxKg);
            var startAgeWk = SampleStartingTrainingAge(rng);
            var deloadInterval = (int)Math.Round(
                ClampedNormal(rng, DeloadIntervalMeanWeeks, DeloadIntervalStdWeeks, 3, 10));
            var nextDeloadWeek = deloadInterval + rng.Next(0, deloadInterval); // jitter the first one
            var pattern = FocusPatterns[rng.Next(FocusPatterns.Length)];
            var blockLen = rng.Next(2, 5); // 2–4 weeks per block, heuristic

            // Pick 5–8 exercises for this athlete's repertoire.
            var exerciseCount = rng.Next(5, 9);
            var picks = CanonicalExercises
                .OrderBy(_ => rng.Next())
                .Take(exerciseCount)
                .ToArray();

            // Initialize the underlying "true" 1RM for each lift at this age.
            var current1Rm = picks.ToDictionary(
                p => p.Name,
                p => StartingOneRm(p.Name, bodyweight, startAgeWk));

            var athleteId = $"synthetic-{athleteIndex:D6}";
            var vectors = new List<WeeklyFeatureVector>(picks.Length * 52);

            for (int w = 0; w < 52; w++)
            {
                var trainingAge = startAgeWk + w;
                var focusIdx = (w / blockLen) % pattern.Length;
                var focus = pattern[focusIdx];
                var isDeload = w >= nextDeloadWeek;
                if (isDeload)
                    nextDeloadWeek = w + (int)Math.Round(
                        ClampedNormal(rng, DeloadIntervalMeanWeeks, DeloadIntervalStdWeeks, 3, 10));

                var weekStart = anchor.AddDays(7 * w);

                foreach (var (exName, muscleGroup) in picks)
                {
                    var overlap     = MuscleOverlap.GetScore(focus, muscleGroup);
                    var muscleScale = MuscleSlopeScale(muscleGroup);
                    var baseSlope   = SquatBaseSlopeKgPerWeek(trainingAge);
                    var deloadMult  = isDeload ? DeloadGainMultiplier : 1.0;
                    var noise       = ClampedNormal(rng, WeeklyDeltaNoiseMean, WeeklyDeltaNoiseStd, 0.2, 1.8);

                    var trueDelta = baseSlope * muscleScale * overlap * deloadMult * noise;

                    // Sample one logged session for this exercise this week.
                    var sets = isDeload ? rng.Next(2, 4) : rng.Next(3, 6);
                    var reps = SampleReps(rng, isDeload);
                    var rpe  = isDeload
                        ? ClampedNormal(rng, RpeMeanDeload, RpeStdDev, RpeMinDeload, RpeMaxDeload)
                        : ClampedNormal(rng, RpeMean,       RpeStdDev, RpeMinNormal, RpeMaxNormal);

                    var oneRm = current1Rm[exName];
                    var weight = WeightFromOneRm(oneRm, reps, rpe);
                    weight = Math.Round(weight / 2.5) * 2.5; // gym plates round to 2.5 kg
                    weight = Math.Max(weight, 0);

                    var observedEst1Rm = Epley(weight, reps);
                    var volume = sets * reps * weight;

                    vectors.Add(new WeeklyFeatureVector
                    {
                        WeekStart        = weekStart,
                        AthleteId        = athleteId,
                        ExerciseName     = exName,
                        MuscleGroup      = muscleGroup,
                        Focus            = focus,
                        VolumeKg         = volume,
                        Est1Rm           = observedEst1Rm,
                        AvgRpe           = rpe,
                        SetCount         = sets,
                        TotalReps        = sets * reps,
                        OverlapScore     = overlap,
                        BodyweightKg     = bodyweight,
                        TrainingAgeWeeks = trainingAge,
                        IsBeginner       = trainingAge < 52,
                        // Lagged fields filled by AppendLabeledPairs.
                    });

                    current1Rm[exName] = Math.Max(0, oneRm + trueDelta);
                }
            }

            return vectors;
        }

        // ── Pair consecutive weeks per (athlete, exercise) into labels ────────

        private static void AppendLabeledPairs(
            List<WeeklyFeatureVector> vectors, List<LabeledTrainingRow> sink)
        {
            var grouped = vectors
                .GroupBy(v => (v.AthleteId, v.ExerciseName))
                .ToList();

            foreach (var g in grouped)
            {
                var ordered = g.OrderBy(v => v.WeekStart).ToList();

                for (int i = 1; i < ordered.Count; i++)
                {
                    ordered[i].PrevWeekEst1Rm   = ordered[i - 1].Est1Rm;
                    ordered[i].PrevWeekVolumeKg = ordered[i - 1].VolumeKg;
                }

                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    sink.Add(new LabeledTrainingRow
                    {
                        Features            = ordered[i],
                        NextWeekEst1RmDelta = ordered[i + 1].Est1Rm - ordered[i].Est1Rm,
                    });
                }
            }
        }

        // ── Strength curves ───────────────────────────────────────────────────

        // Squat-equivalent base slope (kg/week) interpolated from calcffmi
        // tier-end standards for an 82 kg reference lifter. The shape is
        // front-loaded in novice, then flattens into elite.
        private static double SquatBaseSlopeKgPerWeek(int trainingAgeWeeks) =>
            trainingAgeWeeks switch
            {
                < NoviceEnd       => Lerp(3.0,  1.5, trainingAgeWeeks / (double)NoviceEnd),
                < BeginnerEnd     => Lerp(1.5,  0.7, (trainingAgeWeeks - NoviceEnd)       / (double)(BeginnerEnd - NoviceEnd)),
                < IntermediateEnd => Lerp(0.7,  0.5, (trainingAgeWeeks - BeginnerEnd)     / (double)(IntermediateEnd - BeginnerEnd)),
                < AdvancedEnd     => Lerp(0.5,  0.3, (trainingAgeWeeks - IntermediateEnd) / (double)(AdvancedEnd - IntermediateEnd)),
                _                 => Math.Max(0.05, 0.30 * Math.Exp(-(trainingAgeWeeks - AdvancedEnd) / 260.0)),
            };

        // Per-muscle scaling on the squat-equivalent slope. Derived from the
        // ratio of bench/OHP gain to squat gain in the calcffmi tables (bench
        // progresses ≈ half as fast as squat over the same window).
        private static double MuscleSlopeScale(string muscleGroup) =>
            muscleGroup switch
            {
                "Legs"      => 1.00,
                "Back"      => 0.95, // deadlift-class
                "Chest"     => 0.50,
                "Shoulders" => 0.40,
                "Arms"      => 0.45, // heuristic
                "Core"      => 0.30, // heuristic
                _           => 0.50,
            };

        // Starting 1RM as a multiple of bodyweight, interpolated at the
        // tier-end anchors from calcffmi standards.
        private static double StartingOneRm(string exerciseName, double bodyweightKg, int trainingAgeWeeks)
        {
            // (novice, beginner, intermediate, advanced, elite) multiples.
            var (n, b, i, a, e) = exerciseName switch
            {
                "Squat" or "Romanian Deadlift" => (0.86, 1.44, 1.97, 2.61, 3.22),
                "Leg Press"                    => (1.50, 2.50, 3.50, 4.50, 5.50), // heuristic — typically 1.5–2× squat
                "Deadlift"                     => (1.06, 1.69, 2.31, 2.97, 3.61),
                "Barbell Row" or "Lat Pulldown"=> (0.55, 0.85, 1.15, 1.45, 1.75), // heuristic — ~half of deadlift
                "Bench Press" or "Incline Bench Press" => (0.64, 0.94, 1.31, 1.69, 2.11),
                "Overhead Press"               => (0.42, 0.61, 0.83, 1.08, 1.33),
                "Barbell Curl"                 => (0.25, 0.40, 0.55, 0.70, 0.85), // heuristic
                "Tricep Pushdown"              => (0.30, 0.45, 0.60, 0.75, 0.90), // heuristic
                _                              => (0.50, 0.80, 1.00, 1.20, 1.40),
            };

            double multiple = trainingAgeWeeks switch
            {
                <  NoviceEnd       => Lerp(n * 0.5, n, trainingAgeWeeks / (double)NoviceEnd),
                <  BeginnerEnd     => Lerp(n, b, (trainingAgeWeeks - NoviceEnd)       / (double)(BeginnerEnd - NoviceEnd)),
                <  IntermediateEnd => Lerp(b, i, (trainingAgeWeeks - BeginnerEnd)     / (double)(IntermediateEnd - BeginnerEnd)),
                <  AdvancedEnd     => Lerp(i, a, (trainingAgeWeeks - IntermediateEnd) / (double)(AdvancedEnd - IntermediateEnd)),
                <  520             => Lerp(a, e, (trainingAgeWeeks - AdvancedEnd)     / (double)(520 - AdvancedEnd)),
                _                  => e,
            };

            return bodyweightKg * multiple;
        }

        // Weight that produces the given (reps, RPE) for an athlete with this 1RM.
        // RPE → RIR: RIR = 10 - RPE.  Total reps in reserve at failure = reps + RIR.
        // Epley inverse: weight = 1RM × 30 / (30 + reps + RIR).
        private static double WeightFromOneRm(double oneRm, int reps, double rpe)
        {
            var rir = Math.Max(0, 10.0 - rpe);
            var denom = 30.0 + reps + rir;
            return oneRm * 30.0 / denom;
        }

        private static double Epley(double weightKg, int reps) =>
            reps <= 0 ? 0 : weightKg * (1.0 + reps / 30.0);

        // ── Sampling helpers ──────────────────────────────────────────────────

        private static int SampleStartingTrainingAge(Random rng)
        {
            // Pick a tier band by weight, then a uniform week within it.
            var roll = rng.NextDouble();
            double cum = 0;
            foreach (var (lo, hi, w) in TierMix)
            {
                cum += w;
                if (roll <= cum) return rng.Next(lo, hi);
            }
            return TierMix[^1].MaxAgeWk - 1;
        }

        // Bias rep counts toward common rep schemes (5, 8, 10, 12).
        // heuristic but mirrors how real programs are written.
        private static int SampleReps(Random rng, bool isDeload)
        {
            if (isDeload)
                return new[] { 5, 6, 8 }[rng.Next(3)];
            // Weighted distribution: 5×reps strength bias, 8/10/12 hypertrophy bias
            var roll = rng.NextDouble();
            return roll switch
            {
                < 0.15 => 3,
                < 0.40 => 5,
                < 0.55 => 6,
                < 0.75 => 8,
                < 0.90 => 10,
                _      => 12,
            };
        }

        // Box-Muller normal sampler, clamped to [min, max].
        private static double ClampedNormal(Random rng, double mean, double std, double min, double max)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double sample = mean + z * std;
            return Math.Clamp(sample, min, max);
        }

        private static double Lerp(double a, double b, double t) =>
            a + (b - a) * Math.Clamp(t, 0.0, 1.0);
    }
}
