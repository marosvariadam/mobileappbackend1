using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UserController : ControllerBase
    {
        private readonly UserService _userService;

        public UserController(UserService userService)
        {
            _userService = userService;
        }

        // ── Trainer: view their roster ────────────────────────────────────────

        /// <summary>
        /// Returns the trainer's own athlete roster, sorted alphabetically, paged.
        /// A trainer cannot see athletes belonging to another trainer.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<User>>> GetRoster(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athletes = await _userService.GetAthletesByTrainerIdAsync(trainerId, page, pageSize);
            return Ok(athletes);
        }

        /// <summary>Alias for GET /api/user — kept for client backward compatibility.</summary>
        [HttpGet("my-athletes")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<User>>> GetMyAthletes(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athletes = await _userService.GetAthletesByTrainerIdAsync(trainerId, page, pageSize);
            return Ok(athletes);
        }

        /// <summary>
        /// Get a user profile.
        /// - Trainers can view their own profile and any of their athletes' profiles.
        /// - Athletes can only view their own profile.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<User>> Get(string id)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();

            if (User.IsInRole("Trainer"))
            {
                // Trainer may only see themselves or their own athletes
                if (user.Id != currentUserId && user.TrainerId != currentUserId)
                    return Forbid();
            }
            else
            {
                // Athletes may only see their own profile
                if (currentUserId != id)
                    return Forbid();
            }

            return Ok(user);
        }

        // ── Trainer registration (public) ─────────────────────────────────────

        /// <summary>
        /// Public self-registration for trainers only.
        /// Athletes are created by their trainer — they cannot self-register.
        /// </summary>
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterTrainerRequest request)
        {
            try
            {
                var trainer = new User
                {
                    FirstName = request.FirstName,
                    LastName  = request.LastName,
                    Email     = request.Email,
                    Role      = UserRole.Trainer   // always Trainer — never accepted from client
                };

                await _userService.CreateAsync(trainer, request.Password);
                return CreatedAtAction(nameof(Get), new { id = trainer.Id }, trainer);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        // ── Trainer: manage athletes ──────────────────────────────────────────

        /// <summary>
        /// Trainer creates an athlete account and automatically links it to themselves.
        /// The trainer sets the athlete's initial password.
        /// The athlete should change it after first login via POST /api/user/change-password.
        /// </summary>
        [HttpPost("athletes")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> AddAthlete([FromBody] AddAthleteRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            try
            {
                var athlete = new User
                {
                    FirstName = request.FirstName,
                    LastName  = request.LastName,
                    Email     = request.Email,
                    Role      = UserRole.Athlete,  // always Athlete — never accepted from client
                    TrainerId = trainerId          // auto-assigned from JWT
                };

                await _userService.CreateAsync(athlete, request.Password);
                return CreatedAtAction(nameof(Get), new { id = athlete.Id }, athlete);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Trainer updates one of their athlete's name or email.
        /// </summary>
        [HttpPut("athletes/{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> UpdateAthlete(string id, [FromBody] UpdateProfileRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var athlete   = await _userService.GetByIdAsync(id);
            if (athlete == null) return NotFound();

            // Ensure the athlete belongs to this trainer
            if (athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            // TrainerId is preserved — trainers cannot reassign an athlete to someone else here
            await _userService.UpdateAsync(id, request.FirstName, request.LastName, request.Email, trainerId);
            return NoContent();
        }

        /// <summary>
        /// Trainer resets an athlete's password without needing the old one.
        /// The athlete's active refresh token is revoked so they must log in again.
        /// </summary>
        [HttpPost("athletes/{id}/reset-password")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> ResetAthletePassword(
            string id, [FromBody] ResetPasswordRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var athlete   = await _userService.GetByIdAsync(id);
            if (athlete == null) return NotFound();

            if (athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            await _userService.SetPasswordAsync(id, request.NewPassword);
            await _userService.RevokeRefreshTokenAsync(id);
            return NoContent();
        }

        /// <summary>
        /// Trainer removes an athlete from their roster (deletes the athlete's account).
        /// </summary>
        [HttpDelete("athletes/{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> RemoveAthlete(string id)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var athlete   = await _userService.GetByIdAsync(id);
            if (athlete == null) return NotFound();

            if (athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            await _userService.RemoveAsync(id);
            return NoContent();
        }

        // ── Self-service (any authenticated user) ─────────────────────────────

        /// <summary>
        /// User updates their own name or email.
        /// TrainerId is never changeable through this endpoint — only a trainer
        /// can link an athlete to themselves.
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] UpdateProfileRequest request)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (currentUserId != id) return Forbid();

            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();

            // Preserve the existing TrainerId — the user cannot reassign themselves
            await _userService.UpdateAsync(id, request.FirstName, request.LastName, request.Email, user.TrainerId);
            return NoContent();
        }

        /// <summary>
        /// User changes their own password.
        /// Requires the current password for verification.
        /// Revokes all active refresh tokens on success.
        /// </summary>
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var success = await _userService.ChangePasswordAsync(
                currentUserId, request.CurrentPassword, request.NewPassword);

            if (!success)
                return BadRequest(new { message = "Current password is incorrect." });

            await _userService.RevokeRefreshTokenAsync(currentUserId);
            return NoContent();
        }

        /// <summary>
        /// User deletes their own account.
        /// Trainers use DELETE /api/user/athletes/{id} to remove an athlete.
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (currentUserId != id) return Forbid();

            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();

            await _userService.RemoveAsync(id);
            return NoContent();
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    /// <summary>Trainer self-registration. Role is always set server-side to Trainer.</summary>
    public class RegisterTrainerRequest
    {
        [Required] [MaxLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required] [MaxLength(100)] public string LastName  { get; set; } = string.Empty;
        [Required] [EmailAddress]  [MaxLength(256)] public string Email { get; set; } = string.Empty;
        [Required] [MinLength(8)]  public string Password   { get; set; } = string.Empty;
    }

    /// <summary>
    /// Trainer creates an athlete account.
    /// Role is always Athlete and TrainerId is taken from the trainer's JWT — neither
    /// field is accepted from the client body.
    /// </summary>
    public class AddAthleteRequest
    {
        [Required] [MaxLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required] [MaxLength(100)] public string LastName  { get; set; } = string.Empty;
        [Required] [EmailAddress]  [MaxLength(256)] public string Email { get; set; } = string.Empty;
        /// <summary>Initial password — athlete should change this after first login.</summary>
        [Required] [MinLength(8)]  public string Password   { get; set; } = string.Empty;
    }

    /// <summary>Update name or email. Used for both self-updates and trainer-updates-athlete.</summary>
    public class UpdateProfileRequest
    {
        [Required] [MaxLength(100)] public string FirstName { get; set; } = string.Empty;
        [Required] [MaxLength(100)] public string LastName  { get; set; } = string.Empty;
        [Required] [EmailAddress]  [MaxLength(256)] public string Email { get; set; } = string.Empty;
    }

    /// <summary>Trainer resets an athlete's password (no old password required).</summary>
    public class ResetPasswordRequest
    {
        [Required] [MinLength(8)] public string NewPassword { get; set; } = string.Empty;
    }

    /// <summary>User changes their own password (old password required for verification).</summary>
    public class ChangePasswordRequest
    {
        [Required]                public string CurrentPassword { get; set; } = string.Empty;
        [Required] [MinLength(8)] public string NewPassword     { get; set; } = string.Empty;
    }
}
