using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    // Persistence seam for PlanGenerationLog. Lives in Services/ so the
    // Planning layer can stay Mongo-free; PlanRepairService returns the log
    // in-memory, and callers (controllers, the evaluation harness) hand it to
    // this service for storage.
    public class PlanGenerationLogService
    {
        private readonly IMongoCollection<PlanGenerationLog> _logs;

        public PlanGenerationLogService(IMongoDatabase database)
        {
            _logs = database.GetCollection<PlanGenerationLog>("PlanGenerationLogs");
        }

        public Task InsertAsync(PlanGenerationLog log, CancellationToken ct = default)
            => _logs.InsertOneAsync(log, cancellationToken: ct);

        public Task<PlanGenerationLog?> GetByPlanIdAsync(string planId, CancellationToken ct = default)
            => _logs.Find(l => l.PlanId == planId)
                    .SortByDescending(l => l.CreatedAt)
                    .FirstOrDefaultAsync(ct)!;
    }
}
