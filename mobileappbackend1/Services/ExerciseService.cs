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

        public async Task<List<Exercise>> GetAllAsync()
        {
            return await _exercises.Find(_ => true).ToListAsync();
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
