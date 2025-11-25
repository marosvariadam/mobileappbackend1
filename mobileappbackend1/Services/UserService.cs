using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class UserService
    {
        private readonly IMongoCollection<User> _users;

        public UserService(IMongoDatabase database)
        {
            _users = database.GetCollection<User>("Users");
        }

        public async Task<List<User>> GetAllAsync()
        {
            return await _users.Find(_ => true).ToListAsync();
        }

        public async Task<User?> GetByIdAsync(string id)
        {
            return await _users.Find(u => u.Id == id).FirstOrDefaultAsync();
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _users.Find(u => u.Email == email).FirstOrDefaultAsync();
        }

        public async Task<List<User>> GetAthletesByTrainerIdAsync(string trainerId)
        {
            return await _users.Find(u => u.Role == UserRole.Athlete && u.TrainerId == trainerId)
                               .ToListAsync();
        }


        public async Task CreateAsync(User newUser)
        {
            var existing = await GetByEmailAsync(newUser.Email);
            if (existing != null)
            {
                throw new Exception("Az email már használatban van.");
            }

            await _users.InsertOneAsync(newUser);
        }

        public async Task UpdateAsync(string id, User updatedUser)
        {
            await _users.ReplaceOneAsync(u => u.Id == id, updatedUser);
        }

        public async Task RemoveAsync(string id)
        {
            await _users.DeleteOneAsync(u => u.Id == id);
        }

    }
}
