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
    public class UserController : ControllerBase
    {
        private readonly UserService _userService;
        private readonly TrainerRequestService _trainerRequestService;

        public UserController(UserService userService, TrainerRequestService trainerRequestService)
        {
            _userService = userService;
            _trainerRequestService = trainerRequestService;
        }

        // ── Register ────────────────────────────────────────────────────────────

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
                    return BadRequest(new { message = "A role mező értéke 'Trainer' vagy 'Athlete' lehet." });

                var user = new User
                {
                    FirstName = request.FirstName.Trim(),
                    LastName = request.LastName.Trim(),
                    Email = request.Email.Trim().ToLowerInvariant(),
                    Role = role
                };

                await _userService.CreateAsync(user, request.Password);

                // If athlete provided a trainer email, auto-send a join request
                if (role == UserRole.Athlete && !string.IsNullOrWhiteSpace(request.TrainerEmail))
                {
                    var trainer = await _userService.GetByEmailAsync(request.TrainerEmail.Trim().ToLowerInvariant());
                    if (trainer != null && trainer.Role == UserRole.Trainer)
                    {
                        try
                        {
                            await _trainerRequestService.CreateAsync(
                                user.Id!, trainer.Id!, request.IntroNote);
                        }
                        catch (InvalidOperationException)
                        {
                            // Duplicate request — safe to ignore during registration
                        }
                    }
                }

                return Ok(new { message = "Sikeres regisztráció." });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Email is already in use"))
            {
                return Conflict(new { message = "Ez az email cím már használatban van." });
            }
        }

        // ── Get user by id ──────────────────────────────────────────────────────

        [HttpGet("{id}")]
        [Authorize]
        public async Task<IActionResult> GetUser(string id)
        {
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "Felhasználó nem található." });

            return Ok(new
            {
                id = user.Id,
                firstName = user.FirstName,
                lastName = user.LastName,
                email = user.Email,
                role = user.Role.ToString()
            });
        }

        // ── Update user profile ─────────────────────────────────────────────────

        [HttpPut("{id}")]
        [Authorize]
        public async Task<IActionResult> UpdateUser(string id, [FromBody] UpdateUserRequest request)
        {
            var callerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerId != id)
                return Forbid();

            var existing = await _userService.GetByIdAsync(id);
            if (existing == null)
                return NotFound(new { message = "Felhasználó nem található." });

            await _userService.UpdateAsync(
                id,
                request.FirstName?.Trim() ?? existing.FirstName,
                request.LastName?.Trim() ?? existing.LastName,
                request.Email?.Trim().ToLowerInvariant() ?? existing.Email,
                existing.TrainerId);

            var updated = await _userService.GetByIdAsync(id);
            return Ok(new
            {
                id = updated!.Id,
                firstName = updated.FirstName,
                lastName = updated.LastName,
                email = updated.Email,
                role = updated.Role.ToString()
            });
        }

        // ── Delete user ─────────────────────────────────────────────────────────

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> DeleteUser(string id)
        {
            var callerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerId != id)
                return Forbid();

            await _userService.RemoveAsync(id);
            return NoContent();
        }

        // ── Get trainer's athletes ──────────────────────────────────────────────

        [HttpGet("trainer/{trainerId}/athletes")]
        [Authorize]
        public async Task<IActionResult> GetAthletes(
            string trainerId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            var athletes = await _userService.GetAthletesByTrainerIdAsync(trainerId, page, pageSize);

            return Ok(athletes.Select(a => new
            {
                id = a.Id,
                firstName = a.FirstName,
                lastName = a.LastName,
                email = a.Email
            }));
        }

        // ── Change password ─────────────────────────────────────────────────────

        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId == null)
                return Unauthorized();

            var success = await _userService.ChangePasswordAsync(
                userId, request.CurrentPassword, request.NewPassword);

            if (!success)
                return BadRequest(new { message = "A jelenlegi jelszó helytelen." });

            return Ok(new { message = "Jelszó sikeresen megváltoztatva." });
        }
    }

    // ── Request DTOs ────────────────────────────────────────────────────────────

    public class RegisterRequest
    {
        [Required]
        [MaxLength(100)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string LastName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = string.Empty;

        [EmailAddress]
        public string? TrainerEmail { get; set; }

        [MaxLength(500)]
        public string? IntroNote { get; set; }
    }

    public class UpdateUserRequest
    {
        [MaxLength(100)]
        public string? FirstName { get; set; }

        [MaxLength(100)]
        public string? LastName { get; set; }

        [EmailAddress]
        [MaxLength(256)]
        public string? Email { get; set; }
    }

    public class ChangePasswordRequest
    {
        [Required]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public string NewPassword { get; set; } = string.Empty;
    }
}
