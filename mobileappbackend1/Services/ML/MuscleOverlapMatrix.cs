namespace mobileappbackend1.Services.ML
{
    /// <summary>
    /// Static lookup: how much does training a given muscle group contribute to the target exercise's
    /// strength development?  Coefficients are 0–1 (1 = fully relevant, 0 = unrelated).
    ///
    /// Keys are normalised to lowercase with spaces. MuscleGroup strings from Exercise documents are
    /// normalised with <see cref="NormaliseMuscleGroup"/> before lookup.
    ///
    /// Coefficients are based on primary/secondary muscle involvement research and practical
    /// programming heuristics. They can be tuned without touching anything else.
    /// </summary>
    public static class MuscleOverlapMatrix
    {
        // outer key  = normalised exercise name
        // inner key  = normalised muscle group
        // value      = overlap coefficient 0–1
        private static readonly Dictionary<string, Dictionary<string, float>> _matrix = new()
        {
            ["squat"] = new()
            {
                ["quadriceps"]   = 1.00f,
                ["glutes"]       = 0.90f,
                ["hamstrings"]   = 0.50f,
                ["lower back"]   = 0.40f,
                ["core"]         = 0.30f,
                ["calves"]       = 0.20f,
                ["adductors"]    = 0.40f,
                ["chest"]        = 0.00f,
                ["triceps"]      = 0.00f,
                ["biceps"]       = 0.00f,
                ["shoulders"]    = 0.05f,
                ["lats"]         = 0.10f,
            },
            ["deadlift"] = new()
            {
                ["lower back"]   = 1.00f,
                ["hamstrings"]   = 0.90f,
                ["glutes"]       = 0.85f,
                ["quadriceps"]   = 0.50f,
                ["lats"]         = 0.60f,
                ["traps"]        = 0.50f,
                ["core"]         = 0.40f,
                ["forearms"]     = 0.30f,
                ["biceps"]       = 0.15f,
                ["chest"]        = 0.00f,
                ["triceps"]      = 0.00f,
                ["shoulders"]    = 0.10f,
            },
            ["bench press"] = new()
            {
                ["chest"]        = 1.00f,
                ["triceps"]      = 0.75f,
                ["shoulders"]    = 0.50f,
                ["lats"]         = 0.10f,
                ["core"]         = 0.10f,
                ["quadriceps"]   = 0.00f,
                ["hamstrings"]   = 0.00f,
                ["glutes"]       = 0.00f,
                ["biceps"]       = 0.05f,
            },
            ["overhead press"] = new()
            {
                ["shoulders"]    = 1.00f,
                ["triceps"]      = 0.70f,
                ["traps"]        = 0.40f,
                ["core"]         = 0.35f,
                ["chest"]        = 0.20f,
                ["lats"]         = 0.10f,
                ["quadriceps"]   = 0.00f,
                ["hamstrings"]   = 0.00f,
                ["glutes"]       = 0.00f,
            },
            ["barbell row"] = new()
            {
                ["lats"]         = 1.00f,
                ["biceps"]       = 0.70f,
                ["traps"]        = 0.65f,
                ["lower back"]   = 0.50f,
                ["rear delts"]   = 0.50f,
                ["core"]         = 0.20f,
                ["quadriceps"]   = 0.00f,
                ["chest"]        = 0.00f,
                ["triceps"]      = 0.00f,
            },
        };

        // Aliases: maps common exercise name variations → canonical key above
        private static readonly Dictionary<string, string> _aliases = new()
        {
            ["back squat"]          = "squat",
            ["front squat"]         = "squat",
            ["goblet squat"]        = "squat",
            ["low bar squat"]       = "squat",
            ["high bar squat"]      = "squat",
            ["romanian deadlift"]   = "deadlift",
            ["rdl"]                 = "deadlift",
            ["sumo deadlift"]       = "deadlift",
            ["conventional deadlift"] = "deadlift",
            ["incline bench press"] = "bench press",
            ["flat bench press"]    = "bench press",
            ["dumbbell bench press"] = "bench press",
            ["push press"]          = "overhead press",
            ["ohp"]                 = "overhead press",
            ["military press"]      = "overhead press",
            ["bent over row"]       = "barbell row",
            ["pendlay row"]         = "barbell row",
            ["t-bar row"]           = "barbell row",
        };

        // ── Muscle group category labels for the MuscleFocusDistribution map ──
        private static readonly Dictionary<string, string> _muscleGroupCategory = new()
        {
            ["quadriceps"]  = "legs",
            ["hamstrings"]  = "legs",
            ["glutes"]      = "legs",
            ["calves"]      = "legs",
            ["adductors"]   = "legs",
            ["lower back"]  = "back",
            ["lats"]        = "back",
            ["traps"]       = "back",
            ["rear delts"]  = "back",
            ["chest"]       = "push",
            ["triceps"]     = "push",
            ["shoulders"]   = "push",
            ["biceps"]      = "pull",
            ["forearms"]    = "pull",
            ["core"]        = "core",
        };

        /// <summary>
        /// Returns the overlap coefficient (0–1) between the given muscle group and the target exercise.
        /// Returns 0 if either key is unknown.
        /// </summary>
        public static float GetCoefficient(string exerciseName, string muscleGroup)
        {
            var normExercise = NormaliseExerciseName(exerciseName);
            var normMuscle   = NormaliseMuscleGroup(muscleGroup);

            // Resolve alias
            if (_aliases.TryGetValue(normExercise, out var canonical))
                normExercise = canonical;

            if (!_matrix.TryGetValue(normExercise, out var muscleMap))
                return 0f;

            return muscleMap.TryGetValue(normMuscle, out var coeff) ? coeff : 0f;
        }

        /// <summary>
        /// Maps a muscle group name to a high-level category (legs/push/pull/back/core).
        /// Returns the muscle group as-is if no mapping exists.
        /// </summary>
        public static string GetCategory(string muscleGroup)
        {
            var norm = NormaliseMuscleGroup(muscleGroup);
            return _muscleGroupCategory.TryGetValue(norm, out var cat) ? cat : norm;
        }

        /// <summary>True if the exercise has an entry in the matrix (or an alias to one).</summary>
        public static bool IsKnownExercise(string exerciseName)
        {
            var norm = NormaliseExerciseName(exerciseName);
            if (_aliases.TryGetValue(norm, out var canonical))
                norm = canonical;
            return _matrix.ContainsKey(norm);
        }

        public static string NormaliseExerciseName(string name) =>
            name.Trim().ToLowerInvariant()
                .Replace('-', ' ')
                .Replace('_', ' ');

        public static string NormaliseMuscleGroup(string group) =>
            group.Trim().ToLowerInvariant()
                 .Replace('-', ' ')
                 .Replace('_', ' ');
    }
}
