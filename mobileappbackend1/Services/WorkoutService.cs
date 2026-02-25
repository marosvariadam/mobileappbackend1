using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class WorkoutService
    {
        private readonly IMongoCollection<Workout> _workouts;

        public WorkoutService(IMongoDatabase database)
        {
            _workouts = database.GetCollection<Workout>("Workouts");
        }

        public async Task CreateAsync(Workout newWorkout)
        {
            await _workouts.InsertOneAsync(newWorkout);
        }

        public async Task<Workout?> GetByIdAsync(string id)
        {
            return await _workouts.Find(w => w.Id == id).FirstOrDefaultAsync();
        }

        public async Task<List<Workout>> GetByAthleteIdAsync(string athleteId, int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _workouts.Find(w => w.AthleteId == athleteId)
                                  .SortBy(w => w.ScheduledDate)
                                  .Skip((page - 1) * pageSize)
                                  .Limit(pageSize)
                                  .ToListAsync();
        }

        public async Task<List<Workout>> GetByTrainerIdAsync(string trainerId, int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _workouts.Find(w => w.TrainerId == trainerId)
                                  .SortByDescending(w => w.ScheduledDate)
                                  .Skip((page - 1) * pageSize)
                                  .Limit(pageSize)
                                  .ToListAsync();
        }

        public async Task UpdateAsync(string id, Workout updatedWorkout)
        {
            updatedWorkout.Id = id;
            await _workouts.ReplaceOneAsync(w => w.Id == id, updatedWorkout);
        }

        public async Task ToggleCompletionAsync(string id, bool isCompleted)
        {
            var update = Builders<Workout>.Update.Set(w => w.IsCompleted, isCompleted);
            await _workouts.UpdateOneAsync(w => w.Id == id, update);
        }

        public async Task DeleteAsync(string id)
        {
            await _workouts.DeleteOneAsync(w => w.Id == id);
        }
    }
}
