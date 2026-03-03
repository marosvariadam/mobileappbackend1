using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class TrainerRequestService
    {
        private readonly IMongoCollection<TrainerRequest> _requests;
        private readonly UserService            _userService;
        private readonly NotificationService    _notificationService;
        private readonly OnboardingFormService  _onboardingFormService;

        public TrainerRequestService(
            IMongoDatabase         database,
            UserService            userService,
            NotificationService    notificationService,
            OnboardingFormService  onboardingFormService)
        {
            _requests              = database.GetCollection<TrainerRequest>("TrainerRequests");
            _userService           = userService;
            _notificationService   = notificationService;
            _onboardingFormService = onboardingFormService;
        }

        /// <summary>
        /// Athlete sends a join request to a trainer.
        /// Throws if a pending request to the same trainer already exists.
        /// </summary>
        public async Task<TrainerRequest> CreateAsync(
            string athleteId, string trainerId, string? athleteNote)
        {
            var duplicate = await _requests.Find(r =>
                r.AthleteId == athleteId &&
                r.TrainerId == trainerId &&
                r.Status    == TrainerRequestStatus.Pending).FirstOrDefaultAsync();

            if (duplicate != null)
                throw new InvalidOperationException(
                    "You already have a pending request to this trainer.");

            var request = new TrainerRequest
            {
                AthleteId   = athleteId,
                TrainerId   = trainerId,
                AthleteNote = athleteNote,
                Status      = TrainerRequestStatus.Pending,
                RequestedAt = DateTime.UtcNow
            };
            await _requests.InsertOneAsync(request);

            // Notify trainer
            var athlete     = await _userService.GetByIdAsync(athleteId);
            var athleteName = athlete != null
                ? $"{athlete.FirstName} {athlete.LastName}" : "An athlete";

            await _notificationService.CreateAndSendAsync(
                trainerId,
                NotificationType.TrainerRequestReceived,
                "New Athlete Join Request",
                $"{athleteName} has requested to join your roster.",
                request.Id);

            return request;
        }

        public async Task<List<TrainerRequest>> GetPendingByTrainerIdAsync(string trainerId)
        {
            return await _requests
                .Find(r => r.TrainerId == trainerId && r.Status == TrainerRequestStatus.Pending)
                .SortBy(r => r.RequestedAt)
                .ToListAsync();
        }

        public async Task<List<TrainerRequest>> GetByAthleteIdAsync(string athleteId)
        {
            return await _requests
                .Find(r => r.AthleteId == athleteId)
                .SortByDescending(r => r.RequestedAt)
                .ToListAsync();
        }

        /// <summary>
        /// Trainer accepts the request.
        /// Side-effects (all atomic from the caller's perspective):
        ///   1. Athlete.TrainerId is set.
        ///   2. Request status → Accepted.
        ///   3. Athlete is notified ("request accepted").
        ///   4. If the trainer has an onboarding form, athlete is also notified to fill it in.
        /// </summary>
        public async Task AcceptAsync(string requestId, string trainerId)
        {
            var request = await GetAndValidate(requestId, trainerId);

            // 1. Link athlete to trainer
            await _userService.SetTrainerIdAsync(request.AthleteId, trainerId);

            // 2. Mark accepted
            await _requests.UpdateOneAsync(
                r => r.Id == requestId,
                Builders<TrainerRequest>.Update
                    .Set(r => r.Status,      TrainerRequestStatus.Accepted)
                    .Set(r => r.RespondedAt, DateTime.UtcNow));

            var trainer     = await _userService.GetByIdAsync(trainerId);
            var trainerName = trainer != null
                ? $"{trainer.FirstName} {trainer.LastName}" : "Your trainer";

            // 3. Notify athlete: accepted
            await _notificationService.CreateAndSendAsync(
                request.AthleteId,
                NotificationType.TrainerRequestAccepted,
                "You've Been Accepted!",
                $"{trainerName} has added you to their roster. Welcome!",
                requestId);

            // 4. If trainer has an intake form, prompt the athlete to fill it in
            var form = await _onboardingFormService.GetByTrainerIdAsync(trainerId);
            if (form != null)
            {
                await _notificationService.CreateAndSendAsync(
                    request.AthleteId,
                    NotificationType.OnboardingFormAvailable,
                    "Complete Your Intake Questionnaire",
                    $"{trainerName} has an intake form they'd like you to fill in.",
                    form.Id);
            }
        }

        /// <summary>
        /// Trainer rejects the request. Athlete is notified.
        /// </summary>
        public async Task RejectAsync(string requestId, string trainerId)
        {
            var request = await GetAndValidate(requestId, trainerId);

            await _requests.UpdateOneAsync(
                r => r.Id == requestId,
                Builders<TrainerRequest>.Update
                    .Set(r => r.Status,      TrainerRequestStatus.Rejected)
                    .Set(r => r.RespondedAt, DateTime.UtcNow));

            var trainer     = await _userService.GetByIdAsync(trainerId);
            var trainerName = trainer != null
                ? $"{trainer.FirstName} {trainer.LastName}" : "The trainer";

            await _notificationService.CreateAndSendAsync(
                request.AthleteId,
                NotificationType.TrainerRequestRejected,
                "Request Declined",
                $"{trainerName} was unable to take you on at this time.",
                requestId);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<TrainerRequest> GetAndValidate(string requestId, string trainerId)
        {
            var request = await _requests.Find(r => r.Id == requestId).FirstOrDefaultAsync()
                ?? throw new KeyNotFoundException("Request not found.");

            if (request.TrainerId != trainerId)
                throw new UnauthorizedAccessException();

            if (request.Status != TrainerRequestStatus.Pending)
                throw new InvalidOperationException("This request has already been responded to.");

            return request;
        }
    }
}
