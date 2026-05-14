using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class TrainingBlockService
    {
        private readonly IMongoCollection<TrainingBlock> _blocks;

        public TrainingBlockService(IMongoDatabase database)
        {
            _blocks = database.GetCollection<TrainingBlock>("TrainingBlocks");
        }

        public async Task<TrainingBlock?> GetByIdAsync(string id)
        {
            return await _blocks.Find(b => b.Id == id).FirstOrDefaultAsync();
        }

        // All blocks for an athlete, most recent first.
        public async Task<List<TrainingBlock>> GetByAthleteAsync(string athleteId)
        {
            return await _blocks.Find(b => b.AthleteId == athleteId)
                                .SortByDescending(b => b.StartDate)
                                .ToListAsync();
        }

        // Blocks overlapping a date range — used by feature engineering to resolve
        // the focus for each week in that range.
        public async Task<List<TrainingBlock>> GetByAthleteDateRangeAsync(
            string athleteId, DateTime from, DateTime to)
        {
            var filter = Builders<TrainingBlock>.Filter.And(
                Builders<TrainingBlock>.Filter.Eq(b => b.AthleteId, athleteId),
                Builders<TrainingBlock>.Filter.Lte(b => b.StartDate, to),
                Builders<TrainingBlock>.Filter.Gte(b => b.EndDate, from));

            return await _blocks.Find(filter)
                                .SortBy(b => b.StartDate)
                                .ToListAsync();
        }

        // Overlap check — a block overlaps an existing one iff start <= other.end && end >= other.start.
        // Pass excludeId when updating so the row being updated doesn't collide with itself.
        public async Task<bool> HasOverlapAsync(
            string athleteId, DateTime start, DateTime end, string? excludeId = null)
        {
            var filter = Builders<TrainingBlock>.Filter.And(
                Builders<TrainingBlock>.Filter.Eq(b => b.AthleteId, athleteId),
                Builders<TrainingBlock>.Filter.Lte(b => b.StartDate, end),
                Builders<TrainingBlock>.Filter.Gte(b => b.EndDate, start));

            if (!string.IsNullOrEmpty(excludeId))
                filter &= Builders<TrainingBlock>.Filter.Ne(b => b.Id, excludeId);

            return await _blocks.Find(filter).AnyAsync();
        }

        public async Task CreateAsync(TrainingBlock block)
        {
            await _blocks.InsertOneAsync(block);
        }

        public async Task UpdateAsync(
            string id, string focus, DateTime startDate, DateTime endDate, string? notes)
        {
            var update = Builders<TrainingBlock>.Update
                .Set(b => b.Focus, focus)
                .Set(b => b.StartDate, startDate)
                .Set(b => b.EndDate, endDate)
                .Set(b => b.Notes, notes);

            await _blocks.UpdateOneAsync(b => b.Id == id, update);
        }

        public async Task DeleteAsync(string id)
        {
            await _blocks.DeleteOneAsync(b => b.Id == id);
        }
    }
}
