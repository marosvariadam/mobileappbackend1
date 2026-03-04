using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class OnboardingFormService
    {
        private readonly IMongoCollection<OnboardingForm>     _forms;
        private readonly IMongoCollection<OnboardingResponse> _responses;

        public OnboardingFormService(IMongoDatabase database)
        {
            _forms     = database.GetCollection<OnboardingForm>("OnboardingForms");
            _responses = database.GetCollection<OnboardingResponse>("OnboardingResponses");
        }

        // ── Form (trainer) ────────────────────────────────────────────────────

        /// <summary>
        /// Each trainer has exactly one intake form. Calling this again replaces it.
        /// Server assigns a stable QuestionId to any question that arrives without one.
        /// </summary>
        public async Task<OnboardingForm> UpsertAsync(
            string trainerId, string title,
            string? description, List<OnboardingQuestion> questions)
        {
            // Ensure every question has a stable QuestionId
            foreach (var q in questions)
                if (string.IsNullOrWhiteSpace(q.QuestionId))
                    q.QuestionId = Guid.NewGuid().ToString();

            var existing = await GetByTrainerIdAsync(trainerId);

            if (existing == null)
            {
                var form = new OnboardingForm
                {
                    TrainerId   = trainerId,
                    Title       = title,
                    Description = description,
                    Questions   = questions,
                    CreatedAt   = DateTime.UtcNow
                };
                await _forms.InsertOneAsync(form);
                return form;
            }

            var update = Builders<OnboardingForm>.Update
                .Set(f => f.Title,       title)
                .Set(f => f.Description, description)
                .Set(f => f.Questions,   questions)
                .Set(f => f.UpdatedAt,   DateTime.UtcNow);

            await _forms.UpdateOneAsync(f => f.Id == existing.Id, update);
            existing.Title       = title;
            existing.Description = description;
            existing.Questions   = questions;
            existing.UpdatedAt   = DateTime.UtcNow;
            return existing;
        }

        public async Task<OnboardingForm?> GetByTrainerIdAsync(string trainerId)
        {
            return await _forms.Find(f => f.TrainerId == trainerId).FirstOrDefaultAsync();
        }

        public async Task<OnboardingForm?> GetByIdAsync(string id)
        {
            return await _forms.Find(f => f.Id == id).FirstOrDefaultAsync();
        }

        // ── Response (athlete) ────────────────────────────────────────────────

        /// <summary>
        /// Upsert: an athlete can re-submit to update their answers.
        /// </summary>
        public async Task<OnboardingResponse> SubmitResponseAsync(
            string athleteId, string trainerId,
            string formId, List<AnswerEntry> answers)
        {
            var existing = await _responses
                .Find(r => r.AthleteId == athleteId && r.TrainerId == trainerId)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                await _responses.UpdateOneAsync(
                    r => r.Id == existing.Id,
                    Builders<OnboardingResponse>.Update
                        .Set(r => r.FormId,      formId)
                        .Set(r => r.Answers,     answers)
                        .Set(r => r.SubmittedAt, DateTime.UtcNow));

                existing.FormId      = formId;
                existing.Answers     = answers;
                existing.SubmittedAt = DateTime.UtcNow;
                return existing;
            }

            var response = new OnboardingResponse
            {
                AthleteId   = athleteId,
                TrainerId   = trainerId,
                FormId      = formId,
                Answers     = answers,
                SubmittedAt = DateTime.UtcNow
            };
            await _responses.InsertOneAsync(response);
            return response;
        }

        public async Task<OnboardingResponse?> GetResponseAsync(string athleteId, string trainerId)
        {
            return await _responses
                .Find(r => r.AthleteId == athleteId && r.TrainerId == trainerId)
                .FirstOrDefaultAsync();
        }

        public async Task<List<OnboardingResponse>> GetAllResponsesForTrainerAsync(string trainerId)
        {
            return await _responses
                .Find(r => r.TrainerId == trainerId)
                .SortByDescending(r => r.SubmittedAt)
                .ToListAsync();
        }
    }
}
