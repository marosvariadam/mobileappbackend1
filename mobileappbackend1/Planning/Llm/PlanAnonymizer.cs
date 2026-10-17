using mobileappbackend1.Models;

namespace mobileappbackend1.Planning.Llm
{
    // Strip personally-identifying fields from an AthleteProfile before
    // handing it to the LLM. The generator only needs the training-relevant
    // shape; nothing that ties the row back to a real person is included.
    //
    // What we drop:
    //   - AthleteId (an ObjectId that would correlate across requests)
    //   - SportHistory (free-text; could contain names, locations, dates)
    //   - CreatedAt / UpdatedAt timestamps (another correlation vector)
    //   - Id (Mongo document id)
    // What we keep: Goal, ExperienceLevel, DaysPerWeek, SessionMinutes,
    // AvailableEquipment, AvoidStressTags, ExcludedExerciseIds (opaque to the
    // LLM - they are internal ids it just needs to avoid), MuscleGroupPriority,
    // WeightKg (coarse, no name attached).
    public static class PlanAnonymizer
    {
        public const string AnonymousId = "anonymous";

        public static AthleteProfile Anonymize(AthleteProfile source)
        {
            return new AthleteProfile
            {
                Id                  = null,
                AthleteId           = AnonymousId,
                Goal                = source.Goal,
                ExperienceLevel     = source.ExperienceLevel,
                DaysPerWeek         = source.DaysPerWeek,
                SessionMinutes      = source.SessionMinutes,
                AvailableEquipment  = new List<EquipmentType>(source.AvailableEquipment),
                AvoidStressTags     = new List<string>(source.AvoidStressTags),
                ExcludedExerciseIds = new List<string>(source.ExcludedExerciseIds),
                MuscleGroupPriority = new Dictionary<string, double>(source.MuscleGroupPriority),
                WeightKg            = source.WeightKg,
                SportHistory        = null,
                CreatedAt           = default,
                UpdatedAt           = null,
            };
        }
    }
}
