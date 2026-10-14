using mobileappbackend1.Models;
using MongoDB.Driver;

namespace mobileappbackend1.Services
{
    public class AthleteProfileService
    {
        private readonly IMongoCollection<AthleteProfile> _profiles;

        public AthleteProfileService(IMongoDatabase database)
        {
            _profiles = database.GetCollection<AthleteProfile>("AthleteProfiles");
        }

        public Task<AthleteProfile?> GetByAthleteIdAsync(string athleteId)
            => _profiles.Find(p => p.AthleteId == athleteId).FirstOrDefaultAsync()!;

        // Create-or-replace by AthleteId. Preserves CreatedAt on existing docs,
        // bumps UpdatedAt. Called from the single PUT endpoint - there is no
        // separate POST because every athlete has at most one profile.
        public async Task UpsertAsync(string athleteId, AthleteProfile profile)
        {
            profile.AthleteId = athleteId;
            profile.UpdatedAt = DateTime.UtcNow;

            var existing = await _profiles.Find(p => p.AthleteId == athleteId).FirstOrDefaultAsync();
            if (existing == null)
            {
                profile.Id = null;
                profile.CreatedAt = DateTime.UtcNow;
                await _profiles.InsertOneAsync(profile);
            }
            else
            {
                profile.Id = existing.Id;
                profile.CreatedAt = existing.CreatedAt;
                await _profiles.ReplaceOneAsync(p => p.Id == existing.Id, profile);
            }
        }
    }
}
