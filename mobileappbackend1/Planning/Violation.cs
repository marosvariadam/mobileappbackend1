namespace mobileappbackend1.Planning
{
    public enum Severity { Error, Warning }

    public class Violation
    {
        public string Code { get; init; } = string.Empty;
        public Severity Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public int? DayIndex { get; init; }
        public string? ExerciseId { get; init; }
    }

    public static class ViolationCode
    {
        public const string UnknownExercise      = "UNKNOWN_EXERCISE";
        public const string EquipmentUnavailable = "EQUIPMENT_UNAVAILABLE";
        public const string StressTagConflict    = "STRESS_TAG_CONFLICT";
        public const string ExcludedExercise     = "EXCLUDED_EXERCISE";
        public const string LevelTooHigh         = "LEVEL_TOO_HIGH";
        public const string VolumeBelowMev       = "VOLUME_BELOW_MEV";
        public const string VolumeAboveMrv       = "VOLUME_ABOVE_MRV";
        public const string SessionTooLong       = "SESSION_TOO_LONG";
        public const string DayCountMismatch     = "DAY_COUNT_MISMATCH";
        public const string FrequencyTooLow      = "FREQUENCY_TOO_LOW";
        public const string InvalidPrescription  = "INVALID_PRESCRIPTION";
        public const string PatternImbalance     = "PATTERN_IMBALANCE";
        public const string DuplicateInDay       = "DUPLICATE_IN_DAY";
    }
}
