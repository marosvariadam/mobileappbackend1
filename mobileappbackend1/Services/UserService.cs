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

        public async Task<User?> GetByIdAsync(string id)
        {
            return await _users.Find(u => u.Id == id).FirstOrDefaultAsync();
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _users.Find(u => u.Email == email).FirstOrDefaultAsync();
        }

        public async Task<List<User>> GetAthletesByTrainerIdAsync(
            string trainerId, int page = 1, int pageSize = 50)
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            return await _users
                .Find(u => u.Role == UserRole.Athlete && u.TrainerId == trainerId)
                .SortBy(u => u.LastName)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync();
        }

        public async Task CreateAsync(User newUser, string plainTextPassword)
        {
            var existing = await GetByEmailAsync(newUser.Email);
            if (existing != null)
                throw new InvalidOperationException("Email is already in use.");

            newUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(plainTextPassword);
            newUser.Id = null;
            newUser.CreatedAt = DateTime.UtcNow;

            await _users.InsertOneAsync(newUser);
        }

        // Partial update — only non-sensitive fields; password changes go through ChangePasswordAsync
        public async Task UpdateAsync(string id, string firstName, string lastName, string email, string? trainerId)
        {
            var update = Builders<User>.Update
                .Set(u => u.FirstName, firstName)
                .Set(u => u.LastName, lastName)
                .Set(u => u.Email, email)
                .Set(u => u.TrainerId, trainerId);

            await _users.UpdateOneAsync(u => u.Id == id, update);
        }

        public async Task RemoveAsync(string id)
        {
            await _users.DeleteOneAsync(u => u.Id == id);
        }

        /// <summary>
        /// Links an athlete to a trainer after a join request is accepted.
        /// Only called by TrainerRequestService — not exposed through the API directly.
        /// </summary>
        public async Task SetTrainerIdAsync(string athleteId, string trainerId)
        {
            var update = Builders<User>.Update.Set(u => u.TrainerId, trainerId);
            await _users.UpdateOneAsync(u => u.Id == athleteId, update);
        }

        public async Task<User?> ValidateUserAsync(string email, string password)
        {
            var user = await GetByEmailAsync(email);
            if (user == null) return null;

            if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) return null;

            return user;
        }

        /// <summary>
        /// Overwrites a user's password without requiring the old one.
        /// Intended for trainer-initiated athlete password resets only.
        /// The caller must have already verified ownership before calling this.
        /// </summary>
        public async Task SetPasswordAsync(string userId, string newPassword)
        {
            var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            var update = Builders<User>.Update.Set(u => u.PasswordHash, newHash);
            await _users.UpdateOneAsync(u => u.Id == userId, update);
        }

        // Returns false if userId is not found or currentPassword is wrong
        public async Task<bool> ChangePasswordAsync(string userId, string currentPassword, string newPassword)
        {
            var user = await GetByIdAsync(userId);
            if (user == null) return false;

            if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash)) return false;

            var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            var update = Builders<User>.Update.Set(u => u.PasswordHash, newHash);
            await _users.UpdateOneAsync(u => u.Id == userId, update);
            return true;
        }

        public async Task StoreRefreshTokenAsync(string userId, string tokenHash, DateTime expiry)
        {
            var update = Builders<User>.Update
                .Set(u => u.RefreshTokenHash, tokenHash)
                .Set(u => u.RefreshTokenExpiry, expiry);

            await _users.UpdateOneAsync(u => u.Id == userId, update);
        }

        public async Task RevokeRefreshTokenAsync(string userId)
        {
            var update = Builders<User>.Update
                .Unset(u => u.RefreshTokenHash)
                .Unset(u => u.RefreshTokenExpiry);

            await _users.UpdateOneAsync(u => u.Id == userId, update);
        }
    }
}
