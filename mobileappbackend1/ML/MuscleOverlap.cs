namespace mobileappbackend1.ML
{
    /// <summary>Matrix of how strongly a focus drives adaptation in a muscle group, 0.0-1.0.</summary>
    public static class MuscleOverlap
    {
        private const double Default = 0.10;
        private const double SelfMatch = 1.00;
        private const double NonMatch = 0.05;

        // Muscle groups used in the exercise seed: Chest, Back, Shoulders, Legs, Arms, Core.
        // Focus keys accept compound names ("Push", "Pull", "Upper", "Lower", "Full")
        // plus the muscle-group names themselves. Lookups are case-insensitive.
        private static readonly Dictionary<string, Dictionary<string, double>> Matrix =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["push"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["chest"]     = 1.00,
                    ["shoulders"] = 0.90,
                    ["arms"]      = 0.70,  // triceps-dominant
                    ["core"]      = 0.30,
                    ["back"]      = 0.10,
                    ["legs"]      = 0.05,
                },
                ["pull"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["back"]      = 1.00,
                    ["arms"]      = 0.70,  // biceps-dominant
                    ["shoulders"] = 0.30,  // rear delts
                    ["core"]      = 0.30,
                    ["chest"]     = 0.05,
                    ["legs"]      = 0.05,
                },
                ["legs"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["legs"]      = 1.00,
                    ["core"]      = 0.50,
                    ["back"]      = 0.20,  // deadlift / RDL overlap
                    ["shoulders"] = 0.05,
                    ["arms"]      = 0.05,
                    ["chest"]     = 0.02,
                },
                ["upper"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["chest"]     = 0.80,
                    ["back"]      = 0.80,
                    ["shoulders"] = 0.80,
                    ["arms"]      = 0.70,
                    ["core"]      = 0.40,
                    ["legs"]      = 0.05,
                },
                ["lower"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["legs"]      = 1.00,
                    ["core"]      = 0.60,
                    ["back"]      = 0.15,
                    ["chest"]     = 0.05,
                    ["shoulders"] = 0.05,
                    ["arms"]      = 0.05,
                },
                ["full"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["chest"]     = 0.65,
                    ["back"]      = 0.65,
                    ["shoulders"] = 0.65,
                    ["legs"]      = 0.65,
                    ["arms"]      = 0.55,
                    ["core"]      = 0.55,
                },
            };

        /// <summary>
        /// Returns the overlap score in [0, 1] for a (focus, muscleGroup) pair.
        /// Falls back to self-match approximations when the focus is itself a
        /// muscle-group name (e.g. "Chest"), and to <see cref="Default"/> for
        /// anything unknown so prediction never hard-fails on a typo.
        /// </summary>
        public static double GetScore(string? focus, string? muscleGroup)
        {
            if (string.IsNullOrWhiteSpace(focus) || string.IsNullOrWhiteSpace(muscleGroup))
                return Default;

            if (Matrix.TryGetValue(focus, out var row)
                && row.TryGetValue(muscleGroup, out var score))
                return score;

            // Focus is itself a muscle-group name (e.g. "Chest" block).
            // Matching group - 1.0, else a small non-match bleed.
            return string.Equals(focus, muscleGroup, StringComparison.OrdinalIgnoreCase)
                ? SelfMatch
                : NonMatch;
        }
    }
}
