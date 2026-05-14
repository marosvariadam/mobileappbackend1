using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class MetricsLogService
    {
        private readonly IMongoCollection<MetricsLog> _logs;

        public MetricsLogService(IMongoDatabase database)
        {
            _logs = database.GetCollection<MetricsLog>("MlMetricsLog");
        }

        public Task AppendAsync(MetricsLog log) => _logs.InsertOneAsync(log);

        public Task<MetricsLog?> GetLatestAsync()
            => _logs.Find(FilterDefinition<MetricsLog>.Empty)
                    .SortByDescending(m => m.CreatedAt)
                    .FirstOrDefaultAsync()!;

        public Task<List<MetricsLog>> GetRecentAsync(int limit = 20)
            => _logs.Find(FilterDefinition<MetricsLog>.Empty)
                    .SortByDescending(m => m.CreatedAt)
                    .Limit(limit)
                    .ToListAsync();
    }
}
