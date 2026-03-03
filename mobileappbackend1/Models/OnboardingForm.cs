using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum QuestionType
    {
        /// <summary>Free-text answer.</summary>
        Text,
        /// <summary>Athlete picks one option from a list.</summary>
        MultipleChoice,
        /// <summary>Numeric rating 1 – 10 (e.g. "How would you rate your fitness level?").</summary>
        Scale
    }

    /// <summary>
    /// A trainer's athlete intake form. Each trainer has at most one active form.
    /// The form is delivered to an athlete automatically when the trainer accepts
    /// their join request.
    /// </summary>
    public class OnboardingForm
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [MaxLength(200)]
        public string Title { get; set; } = "Athlete Intake Questionnaire";

        [MaxLength(1000)]
        public string? Description { get; set; }

        public List<OnboardingQuestion> Questions { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
    }

    public class OnboardingQuestion
    {
        /// <summary>Stable identifier used to match answers back to questions.</summary>
        public string QuestionId { get; set; } = Guid.NewGuid().ToString();

        [Required]
        [MaxLength(500)]
        public string Text { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public QuestionType Type { get; set; } = QuestionType.Text;

        /// <summary>Only used when Type == MultipleChoice.</summary>
        public List<string> Options { get; set; } = new();

        public bool IsRequired { get; set; } = true;
    }

    // ── Athlete response ──────────────────────────────────────────────────────

    /// <summary>An athlete's completed answers to their trainer's intake form.</summary>
    public class OnboardingResponse
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string AthleteId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string TrainerId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.ObjectId)]
        public string FormId { get; set; } = string.Empty;

        public List<AnswerEntry> Answers { get; set; } = new();

        public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    }

    public class AnswerEntry
    {
        public string QuestionId { get; set; } = string.Empty;
        public string Answer     { get; set; } = string.Empty;
    }
}
