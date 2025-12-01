using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly UserService _userService;
        private readonly TokenService _tokenService;

        public AuthController(UserService userService, TokenService tokenService)
        {
            _userService = userService;
            _tokenService = tokenService;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var user = await _userService.ValidateUserAsync(request.Email, request.Password);
            if (user == null)
            {
                return Unauthorized("Invalid email or password");
            }

            var token = _tokenService.GenerateToken(user);

            // Return the token AND the user info (so the app knows if they are a trainer)
            return Ok(new { Token = token, UserId = user.Id, Role = user.Role.ToString() });
        }
    }

    public class LoginRequest
    {
        public string Email { get; set; }
        public string Password { get; set; }
    }
}
