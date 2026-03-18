using mobileappbackend1.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class ExerciseService
    {
        private readonly IMongoCollection<Exercise> _exercises;

        public ExerciseService(IMongoDatabase database)
        {
            _exercises = database.GetCollection<Exercise>("Exercises");
        }

        public async Task<List<Exercise>> GetAllAsync(
            int page = 1, int pageSize = 20,
            string? search = null, string? muscleGroup = null)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);

            var filter = Builders<Exercise>.Filter.Empty;

            if (!string.IsNullOrWhiteSpace(search))
                filter &= Builders<Exercise>.Filter.Regex(
                    e => e.Name, new BsonRegularExpression(search, "i"));

            if (!string.IsNullOrWhiteSpace(muscleGroup))
                filter &= Builders<Exercise>.Filter.Eq(e => e.MuscleGroup, muscleGroup);

            return await _exercises.Find(filter)
                                   .SortBy(e => e.Name)
                                   .Skip((page - 1) * pageSize)
                                   .Limit(pageSize)
                                   .ToListAsync();
        }

        public async Task<Exercise?> GetByIdAsync(string id)
        {
            return await _exercises.Find(e => e.Id == id).FirstOrDefaultAsync();
        }

        public async Task CreateAsync(Exercise newExercise)
        {
            await _exercises.InsertOneAsync(newExercise);
        }

        public async Task UpdateAsync(string id, string name, string muscleGroup, string? description, string? equipment)
        {
            var update = Builders<Exercise>.Update
                .Set(e => e.Name, name)
                .Set(e => e.MuscleGroup, muscleGroup)
                .Set(e => e.Description, description ?? string.Empty)
                .Set(e => e.Equipment, equipment ?? string.Empty);

            await _exercises.UpdateOneAsync(e => e.Id == id, update);
        }

        public async Task RemoveAsync(string id)
        {
            await _exercises.DeleteOneAsync(e => e.Id == id);
        }
    }
}
