using mobileappbackend1.Models;
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

        public async Task<List<Exercise>> GetAllAsync(int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _exercises.Find(_ => true)
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

        public async Task RemoveAsync(string id)
        {
            await _exercises.DeleteOneAsync(e => e.Id == id);
        }
    }
}
