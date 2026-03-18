using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace mobileappbackend1.Models
{
    public enum NotificationType
    {
        TrainerRequestReceived,  // trainer receives this when an athlete sends a join request
        TrainerRequestAccepted,  // athlete receives this when trainer accepts
        TrainerRequestRejected,  // athlete receives this when trainer rejects
        OnboardingFormAvailable, // athlete receives this when accepted and a form exists
        OnboardingFormSubmitted, // trainer receives this when athlete completes the survey
        WorkoutAssigned          // athlete receives this when trainer assigns a new workout
    }

    public class Notification
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        /// <summary>The user who should receive this notification.</summary>
        [BsonRepresentation(BsonType.ObjectId)]
        public string UserId { get; set; } = string.Empty;

        [BsonRepresentation(BsonType.String)]
        public NotificationType Type { get; set; }

        public string Title { get; set; } = string.Empty;
        public string Body  { get; set; } = string.Empty;

        public bool IsRead { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// ID of the related entity (TrainerRequest, OnboardingForm, OnboardingResponse, etc.)
        /// so the client can navigate directly to the relevant screen.
        /// </summary>
        public string? ReferenceId { get; set; }
    }
}
