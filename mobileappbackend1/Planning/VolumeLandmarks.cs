using mobileappbackend1.Models;

namespace mobileappbackend1.Planning
{
    // Weekly working-set targets per muscle group per experience level.
    // MEV = minimum effective volume, MAV = maximum adaptive volume,
    // MRV = maximum recoverable volume. Values approximate Renaissance
    // Periodization published landmarks (muscle-group-specific hypertrophy
    // ranges aimed at natural lifters). Adjust centrally when you have
    // better data from your own athletes.
    public static class VolumeLandmarks
    {
        public readonly record struct Range(int Mev, int Mav, int Mrv);

        private static readonly Dictionary<(string, ExperienceLevel), Range> Table = new()
        {
            // Beginner
            [("Chest",     ExperienceLevel.Beginner)]      = new(6,  10, 16),
            [("Back",      ExperienceLevel.Beginner)]      = new(8,  12, 18),
            [("Shoulders", ExperienceLevel.Beginner)]      = new(6,  10, 16),
            [("Legs",      ExperienceLevel.Beginner)]      = new(8,  12, 18),
            [("Arms",      ExperienceLevel.Beginner)]      = new(4,   8, 14),
            [("Core",      ExperienceLevel.Beginner)]      = new(0,   8, 16),

            // Intermediate
            [("Chest",     ExperienceLevel.Intermediate)]  = new(8,  14, 22),
            [("Back",      ExperienceLevel.Intermediate)]  = new(10, 16, 25),
            [("Shoulders", ExperienceLevel.Intermediate)]  = new(8,  14, 22),
            [("Legs",      ExperienceLevel.Intermediate)]  = new(10, 16, 25),
            [("Arms",      ExperienceLevel.Intermediate)]  = new(6,  12, 20),
            [("Core",      ExperienceLevel.Intermediate)]  = new(0,  12, 25),

            // Advanced
            [("Chest",     ExperienceLevel.Advanced)]      = new(10, 18, 26),
            [("Back",      ExperienceLevel.Advanced)]      = new(12, 20, 30),
            [("Shoulders", ExperienceLevel.Advanced)]      = new(10, 18, 26),
            [("Legs",      ExperienceLevel.Advanced)]      = new(12, 20, 30),
            [("Arms",      ExperienceLevel.Advanced)]      = new(8,  16, 25),
            [("Core",      ExperienceLevel.Advanced)]      = new(0,  16, 30),
        };

        // Lookup for a (muscle, level) pair. Returns a permissive default when
        // the muscle group is unknown (unseeded trainer-authored names) so the
        // validator doesn't false-positive on them.
        public static Range For(string muscleGroup, ExperienceLevel level)
        {
            return Table.TryGetValue((muscleGroup, level), out var range)
                ? range
                : new Range(0, 0, int.MaxValue);
        }
    }
}
