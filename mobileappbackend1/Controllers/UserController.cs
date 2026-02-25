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

        // Trainers can list all users (paginated); athletes only see themselves
        [HttpGet]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<User>>> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var users = await _userService.GetAllAsync(page, pageSize);
            return Ok(users);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<User>> Get(string id)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isTrainer = User.IsInRole("Trainer");

            // Users can only retrieve their own profile unless they are a Trainer
            if (!isTrainer && currentUserId != id)
                return Forbid();

            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();
            return Ok(user);
        }

        // A trainer can only retrieve their own athletes
        [HttpGet("my-athletes")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<User>>> GetMyAthletes()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athletes = await _userService.GetAthletesByTrainerIdAsync(trainerId);
            return Ok(athletes);
        }

        // Registration is open to everyone
        [HttpPost("register")]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                var newUser = new User
                {
                    FirstName = request.FirstName,
                    LastName = request.LastName,
                    Email = request.Email,
                    Role = request.Role,
                    TrainerId = request.TrainerId
                };

                await _userService.CreateAsync(newUser, request.Password);
                return CreatedAtAction(nameof(Get), new { id = newUser.Id }, newUser);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        // Users can only update their own profile
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (currentUserId != id)
                return Forbid();

            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();

            await _userService.UpdateAsync(id, request.FirstName, request.LastName, request.Email, request.TrainerId);
            return NoContent();
        }

        // Users can only delete their own account
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (currentUserId != id)
                return Forbid();

            var user = await _userService.GetByIdAsync(id);
            if (user == null) return NotFound();

            await _userService.RemoveAsync(id);
            return NoContent();
        }
    }

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
        public UserRole Role { get; set; }

        public string? TrainerId { get; set; }
    }

    public class UpdateUserRequest
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

        public string? TrainerId { get; set; }
    }
}
