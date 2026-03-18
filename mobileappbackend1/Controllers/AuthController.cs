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
            try
            {
                var user = await _userService.ValidateUserAsync(request.Email, request.Password);
                if (user == null)
                    return Unauthorized(new { message = "Hibás email vagy jelszó." });

                var accessToken = _tokenService.GenerateToken(user);
                var refreshToken = _tokenService.GenerateRefreshToken();
                var refreshTokenHash = _tokenService.HashRefreshToken(refreshToken);

                await _userService.StoreRefreshTokenAsync(user.Id!, refreshTokenHash, DateTime.UtcNow.AddDays(30));

                return Ok(new
                {
                    token = accessToken,
                    refreshToken = refreshToken,
                    userId = user.Id,
                    role = user.Role.ToString()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Szerverhiba a bejelentkezés során: {ex.Message}" });
            }
        }

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
        {
            var tokenHash = _tokenService.HashRefreshToken(request.RefreshToken);
            var user = await _userService.GetByIdAsync(request.UserId);

            if (user == null
                || user.RefreshTokenHash != tokenHash
                || user.RefreshTokenExpiry == null
                || user.RefreshTokenExpiry < DateTime.UtcNow)
            {
                return Unauthorized(new { message = "Invalid or expired refresh token." });
            }

            var newAccessToken = _tokenService.GenerateToken(user);
            var newRefreshToken = _tokenService.GenerateRefreshToken();
            var newRefreshTokenHash = _tokenService.HashRefreshToken(newRefreshToken);

            await _userService.StoreRefreshTokenAsync(user.Id!, newRefreshTokenHash, DateTime.UtcNow.AddDays(30));

            return Ok(new
            {
                Token = newAccessToken,
                RefreshToken = newRefreshToken
            });
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId != null)
                await _userService.RevokeRefreshTokenAsync(userId);

            return NoContent();
        }
    }

    public class LoginRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public string Password { get; set; } = string.Empty;
    }

    public class RefreshRequest
    {
        [Required]
        public string UserId { get; set; } = string.Empty;

        [Required]
        public string RefreshToken { get; set; } = string.Empty;
    }
}
