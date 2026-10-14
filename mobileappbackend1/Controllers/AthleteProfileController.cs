using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/athleteprofile")]
    [Authorize]
    public class AthleteProfileController : ControllerBase
    {
        private readonly AthleteProfileService _profileService;
        private readonly UserService _userService;

        public AthleteProfileController(
            AthleteProfileService profileService,
            UserService userService)
        {
            _profileService = profileService;
            _userService = userService;
        }

        // ── Athlete self ──────────────────────────────────────────────────
        [HttpGet("me")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetMe()
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var profile = await _profileService.GetByAthleteIdAsync(athleteId);
            return profile == null ? NotFound() : Ok(profile);
        }

        [HttpPut("me")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> UpdateMe([FromBody] AthleteProfileRequest request)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            await _profileService.UpsertAsync(athleteId, request.ToProfile());
            var saved = await _profileService.GetByAthleteIdAsync(athleteId);
            return Ok(saved);
        }

        // ── Trainer-of ────────────────────────────────────────────────────
        [HttpGet("{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetForAthlete(string athleteId)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            var profile = await _profileService.GetByAthleteIdAsync(athleteId);
            return profile == null ? NotFound() : Ok(profile);
        }

        [HttpPut("{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> UpdateForAthlete(
            string athleteId, [FromBody] AthleteProfileRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            await _profileService.UpsertAsync(athleteId, request.ToProfile());
            var saved = await _profileService.GetByAthleteIdAsync(athleteId);
            return Ok(saved);
        }
    }

    public class AthleteProfileRequest
    {
        [Required] public Goal Goal { get; set; } = Goal.GeneralFitness;
        [Required] public ExperienceLevel ExperienceLevel { get; set; } = ExperienceLevel.Beginner;

        [Range(2, 6)] public int DaysPerWeek { get; set; } = 3;
        [Range(30, 120)] public int SessionMinutes { get; set; } = 60;

        public List<EquipmentType>? AvailableEquipment { get; set; }
        public List<string>? AvoidStressTags { get; set; }
        public List<string>? ExcludedExerciseIds { get; set; }
        public Dictionary<string, double>? MuscleGroupPriority { get; set; }
        public double? WeightKg { get; set; }

        [MaxLength(2000)]
        public string? SportHistory { get; set; }

        public AthleteProfile ToProfile() => new()
        {
            Goal                = Goal,
            ExperienceLevel     = ExperienceLevel,
            DaysPerWeek         = DaysPerWeek,
            SessionMinutes      = SessionMinutes,
            AvailableEquipment  = AvailableEquipment  ?? new List<EquipmentType>(),
            AvoidStressTags     = AvoidStressTags     ?? new List<string>(),
            ExcludedExerciseIds = ExcludedExerciseIds ?? new List<string>(),
            MuscleGroupPriority = MuscleGroupPriority ?? new Dictionary<string, double>(),
            WeightKg            = WeightKg,
            SportHistory        = SportHistory,
        };
    }
}
