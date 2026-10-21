using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    // Mongo CRUD for TrainingPlan. Thin: domain logic lives in Planning/.
    public class PlanService
    {
        private readonly IMongoCollection<TrainingPlan> _plans;

        public PlanService(IMongoDatabase database)
        {
            _plans = database.GetCollection<TrainingPlan>("TrainingPlans");
        }

        public Task CreateAsync(TrainingPlan plan, CancellationToken ct = default)
            => _plans.InsertOneAsync(plan, cancellationToken: ct);

        public Task<TrainingPlan?> GetByIdAsync(string id, CancellationToken ct = default)
            => _plans.Find(p => p.Id == id).FirstOrDefaultAsync(ct)!;

        public Task<List<TrainingPlan>> GetByAthleteIdAsync(string athleteId, CancellationToken ct = default)
            => _plans.Find(p => p.AthleteId == athleteId)
                     .SortByDescending(p => p.CreatedAt)
                     .ToListAsync(ct);

        public Task<List<TrainingPlan>> GetAcceptedByAthleteIdAsync(
            string athleteId, CancellationToken ct = default)
            => _plans.Find(p => p.AthleteId == athleteId && p.Status != PlanStatus.Draft)
                     .SortByDescending(p => p.CreatedAt)
                     .ToListAsync(ct);

        // Replace the mutable part of a plan (days + metadata). Status / IDs
        // are preserved so refine / manual edits can't accidentally un-accept
        // a plan or orphan it from its training block.
        public Task ReplaceDaysAsync(
            string id, string title, int weeks, SplitType split,
            List<PlanDay> days, CancellationToken ct = default)
        {
            var update = Builders<TrainingPlan>.Update
                .Set(p => p.Title, title)
                .Set(p => p.Weeks, weeks)
                .Set(p => p.Split, split)
                .Set(p => p.Days, days);
            return _plans.UpdateOneAsync(p => p.Id == id, update, cancellationToken: ct);
        }

        public Task MarkAcceptedAsync(string id, DateTime at, CancellationToken ct = default)
        {
            var update = Builders<TrainingPlan>.Update
                .Set(p => p.Status, PlanStatus.Accepted)
                .Set(p => p.AcceptedAt, at);
            return _plans.UpdateOneAsync(p => p.Id == id, update, cancellationToken: ct);
        }

        public Task MarkMaterializedAsync(string id, CancellationToken ct = default)
        {
            var update = Builders<TrainingPlan>.Update
                .Set(p => p.Status, PlanStatus.Materialized);
            return _plans.UpdateOneAsync(p => p.Id == id, update, cancellationToken: ct);
        }
    }
}
