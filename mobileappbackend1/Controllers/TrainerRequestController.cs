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
    public class TrainerRequestController : ControllerBase
    {
        private readonly TrainerRequestService _requestService;
        private readonly UserService _userService;

        public TrainerRequestController(
            TrainerRequestService requestService,
            UserService userService)
        {
            _requestService = requestService;
            _userService    = userService;
        }

        // ── Athlete ───────────────────────────────────────────────────────────

        /// <summary>
        /// Athlete sends a join request to a trainer identified by email address.
        /// An optional note can be included (e.g. goals, experience summary).
        /// A real-time notification is pushed to the trainer immediately.
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> SendRequest([FromBody] SendTrainerRequestRequest request)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var trainer = await _userService.GetByEmailAsync(request.TrainerEmail);
            if (trainer == null || trainer.Role != UserRole.Trainer)
                return NotFound(new { message = "No trainer found with that email address." });

            // Prevent sending a request to a trainer they're already linked to
            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete?.TrainerId == trainer.Id)
                return Conflict(new { message = "You are already linked to this trainer." });

            try
            {
                var result = await _requestService.CreateAsync(athleteId, trainer.Id!, request.Note);
                return CreatedAtAction(nameof(GetMyRequests), null, result);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Athlete views all of their own join requests (any status).
        /// </summary>
        [HttpGet("mine")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<List<TrainerRequest>>> GetMyRequests()
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            return Ok(await _requestService.GetByAthleteIdAsync(athleteId));
        }

        // ── Trainer ───────────────────────────────────────────────────────────

        /// <summary>
        /// Trainer views all pending join requests, sorted oldest-first.
        /// </summary>
        [HttpGet("pending")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<TrainerRequest>>> GetPending()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            return Ok(await _requestService.GetPendingByTrainerIdAsync(trainerId));
        }

        /// <summary>
        /// Trainer accepts a join request.
        /// The athlete is immediately linked to the trainer's roster.
        /// The athlete is notified and, if a form exists, prompted to fill it in.
        /// </summary>
        [HttpPatch("{id}/accept")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Accept(string id)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            try
            {
                await _requestService.AcceptAsync(id, trainerId);
                return NoContent();
            }
            catch (KeyNotFoundException)         { return NotFound(); }
            catch (UnauthorizedAccessException)  { return Forbid(); }
            catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        }

        /// <summary>
        /// Trainer rejects a join request. The athlete is notified.
        /// </summary>
        [HttpPatch("{id}/reject")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Reject(string id)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            try
            {
                await _requestService.RejectAsync(id, trainerId);
                return NoContent();
            }
            catch (KeyNotFoundException)         { return NotFound(); }
            catch (UnauthorizedAccessException)  { return Forbid(); }
            catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
        }
    }

    public class SendTrainerRequestRequest
    {
        /// <summary>The trainer's registered email address.</summary>
        [Required]
        [EmailAddress]
        public string TrainerEmail { get; set; } = string.Empty;

        /// <summary>
        /// Optional message to the trainer — e.g. goals, sport background, availability.
        /// </summary>
        [MaxLength(500)]
        public string? Note { get; set; }
    }
}
