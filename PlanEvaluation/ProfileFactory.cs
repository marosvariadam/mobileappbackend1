using mobileappbackend1.Models;

namespace PlanEvaluation;

// Seeded, realistic AthleteProfile generator. The seed drives every random
// choice so a given profile index always materializes the same way across
// runs — essential for a thesis evaluation that other people need to be
// able to reproduce.
internal static class ProfileFactory
{
    // Keep core equipment (Barbell + Dumbbell + Bodyweight) in every
    // profile. The constraint generator needs enough variety to hit MEVs
    // and compound-per-day without timing out on over-constrained inputs.
    private static readonly string[] StressTagPool =
        { "knee", "shoulder", "elbow", "wrist", "lower_back", "hip", "ankle" };

    public static AthleteProfile Build(int seed)
    {
        var rng = new Random(seed);

        var equipment = new List<EquipmentType>
        {
            EquipmentType.Barbell, EquipmentType.Dumbbell, EquipmentType.Bodyweight,
        };
        if (rng.NextDouble() < 0.85) equipment.Add(EquipmentType.Machine);
        if (rng.NextDouble() < 0.75) equipment.Add(EquipmentType.Cable);
        if (rng.NextDouble() < 0.35) equipment.Add(EquipmentType.Kettlebell);
        if (rng.NextDouble() < 0.30) equipment.Add(EquipmentType.Band);

        // ~20% of athletes carry one stress-tagged injury the generator
        // must respect. Keeping it at one keeps most profiles feasible.
        var avoidTags = new List<string>();
        if (rng.NextDouble() < 0.20)
            avoidTags.Add(StressTagPool[rng.Next(StressTagPool.Length)]);

        // Light muscle priority spread so a chunk of profiles exercise the
        // priority-weighting path in PlanScoringService without biasing
        // every row toward the same group.
        var priorities = new Dictionary<string, double>();
        if (rng.NextDouble() < 0.40)
        {
            var muscles = new[] { "Chest", "Back", "Shoulders", "Legs", "Arms" };
            var pick = muscles[rng.Next(muscles.Length)];
            priorities[pick] = Math.Round(1.0 + (rng.NextDouble() * 0.5), 1);  // 1.0..1.5
        }

        return new AthleteProfile
        {
            AthleteId           = $"eval-{seed:D4}",
            Goal                = (Goal)rng.Next(0, 4),
            ExperienceLevel     = (ExperienceLevel)rng.Next(0, 3),
            DaysPerWeek         = rng.Next(2, 7),              // 2..6 inclusive
            SessionMinutes      = rng.Next(60, 121),           // 60..120
            AvailableEquipment  = equipment,
            AvoidStressTags     = avoidTags,
            ExcludedExerciseIds = new List<string>(),
            MuscleGroupPriority = priorities,
            WeightKg            = Math.Round(55 + rng.NextDouble() * 50, 1),  // 55..105 kg
        };
    }
}
