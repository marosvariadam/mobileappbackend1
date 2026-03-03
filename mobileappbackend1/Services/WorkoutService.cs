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

        // Athlete: all their sessions, paged
        public async Task<List<Workout>> GetByAthleteIdAsync(string athleteId, int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _workouts.Find(w => w.AthleteId == athleteId)
                                  .SortBy(w => w.ScheduledDate)
                                  .Skip((page - 1) * pageSize)
                                  .Limit(pageSize)
                                  .ToListAsync();
        }

        // Trainer: all sessions they created, paged
        public async Task<List<Workout>> GetByTrainerIdAsync(string trainerId, int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _workouts.Find(w => w.TrainerId == trainerId)
                                  .SortByDescending(w => w.ScheduledDate)
                                  .Skip((page - 1) * pageSize)
                                  .Limit(pageSize)
                                  .ToListAsync();
        }

        // Athlete: sessions within a date window for the calendar view
        public async Task<List<Workout>> GetByDateRangeAsync(string athleteId, DateTime from, DateTime to)
        {
            return await _workouts
                .Find(w => w.AthleteId == athleteId && w.ScheduledDate >= from && w.ScheduledDate <= to)
                .SortBy(w => w.ScheduledDate)
                .ToListAsync();
        }

        // Trainer: all sessions they created within a date window for their own calendar view
        public async Task<List<Workout>> GetByDateRangeForTrainerAsync(
            string trainerId, DateTime from, DateTime to)
        {
            return await _workouts
                .Find(w => w.TrainerId == trainerId && w.ScheduledDate >= from && w.ScheduledDate <= to)
                .SortBy(w => w.ScheduledDate)
                .ToListAsync();
        }

        // Trainer: review completed sessions, optionally filtered to one athlete
        public async Task<List<Workout>> GetCompletedByTrainerIdAsync(
            string trainerId, string? athleteId, int page = 1, int pageSize = 20)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);

            var filter = Builders<Workout>.Filter.And(
                Builders<Workout>.Filter.Eq(w => w.TrainerId, trainerId),
                Builders<Workout>.Filter.Eq(w => w.Status, WorkoutStatus.Completed));

            if (!string.IsNullOrEmpty(athleteId))
                filter &= Builders<Workout>.Filter.Eq(w => w.AthleteId, athleteId);

            return await _workouts.Find(filter)
                                  .SortByDescending(w => w.CompletedAt)
                                  .Skip((page - 1) * pageSize)
                                  .Limit(pageSize)
                                  .ToListAsync();
        }

        // Trainer: edit session title, notes, difficulty, date, exercise list — only while status is Planned
        public async Task UpdateAsync(
            string id, string title, string? trainerNotes,
            DifficultyLevel difficulty, DateTime scheduledDate, List<WorkoutExercise> exercises)
        {
            var update = Builders<Workout>.Update
                .Set(w => w.Title, title)
                .Set(w => w.TrainerNotes, trainerNotes)
                .Set(w => w.Difficulty, difficulty)
                .Set(w => w.ScheduledDate, scheduledDate)
                .Set(w => w.Exercises, exercises);

            await _workouts.UpdateOneAsync(w => w.Id == id, update);
        }

        // Athlete: transition Planned → InProgress
        public async Task StartAsync(string id)
        {
            var update = Builders<Workout>.Update
                .Set(w => w.Status, WorkoutStatus.InProgress)
                .Set(w => w.StartedAt, DateTime.UtcNow);

            await _workouts.UpdateOneAsync(w => w.Id == id, update);
        }

        // Athlete: log actual results for one exercise by its index in the list
        public async Task LogExerciseAsync(
            string workoutId, int exerciseIndex,
            int actualSets, int actualRepetitions, double actualWeightKg, string? athleteNotes)
        {
            var prefix = $"Exercises.{exerciseIndex}";
            var update = Builders<Workout>.Update
                .Set($"{prefix}.ActualSets", actualSets)
                .Set($"{prefix}.ActualRepetitions", actualRepetitions)
                .Set($"{prefix}.ActualWeightKg", actualWeightKg)
                .Set($"{prefix}.AthleteNotes", athleteNotes)
                .Set($"{prefix}.IsCompleted", true);

            await _workouts.UpdateOneAsync(w => w.Id == workoutId, update);
        }

        // Athlete: transition InProgress → Completed, attach session-level feedback
        public async Task CompleteWithFeedbackAsync(string id, string? athleteFeedback)
        {
            var update = Builders<Workout>.Update
                .Set(w => w.Status, WorkoutStatus.Completed)
                .Set(w => w.CompletedAt, DateTime.UtcNow)
                .Set(w => w.AthleteFeedback, athleteFeedback);

            await _workouts.UpdateOneAsync(w => w.Id == id, update);
        }

        public async Task DeleteAsync(string id)
        {
            await _workouts.DeleteOneAsync(w => w.Id == id);
        }
    }
}
