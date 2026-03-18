using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/trainer-request")]
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

        // ── Helper: enrich a TrainerRequest with names/emails ───────────────────

        private async Task<object> MapRequest(TrainerRequest r)
        {
            var athlete = await _userService.GetByIdAsync(r.AthleteId);
            var trainer = await _userService.GetByIdAsync(r.TrainerId);

            return new
            {
                id           = r.Id,
                athleteId    = r.AthleteId,
                athleteName  = athlete != null ? $"{athlete.FirstName} {athlete.LastName}" : (string?)null,
                athleteEmail = athlete?.Email,
                trainerEmail = trainer?.Email,
                status       = r.Status.ToString(),
                note         = r.AthleteNote,
                createdAt    = r.RequestedAt
            };
        }

        private async Task<List<object>> MapRequests(List<TrainerRequest> requests)
        {
            var userIds = requests
                .SelectMany(r => new[] { r.AthleteId, r.TrainerId })
                .Distinct()
                .ToList();

            var users = new Dictionary<string, User>();
            foreach (var uid in userIds)
            {
                var u = await _userService.GetByIdAsync(uid);
                if (u != null) users[uid] = u;
            }

            return requests.Select(r =>
            {
                users.TryGetValue(r.AthleteId, out var athlete);
                users.TryGetValue(r.TrainerId, out var trainer);
                return (object)new
                {
                    id           = r.Id,
                    athleteId    = r.AthleteId,
                    athleteName  = athlete != null ? $"{athlete.FirstName} {athlete.LastName}" : (string?)null,
                    athleteEmail = athlete?.Email,
                    trainerEmail = trainer?.Email,
                    status       = r.Status.ToString(),
                    note         = r.AthleteNote,
                    createdAt    = r.RequestedAt
                };
            }).ToList();
        }

        // ── Athlete ───────────────────────────────────────────────────────────

        [HttpPost]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> SendRequest([FromBody] SendTrainerRequestRequest request)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var trainer = await _userService.GetByEmailAsync(request.TrainerEmail);
            if (trainer == null || trainer.Role != UserRole.Trainer)
                return NotFound(new { message = "No trainer found with that email address." });

            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete?.TrainerId == trainer.Id)
                return Conflict(new { message = "You are already linked to this trainer." });

            try
            {
                var result = await _requestService.CreateAsync(athleteId, trainer.Id!, request.Note);
                var mapped = await MapRequest(result);
                return CreatedAtAction(nameof(GetMyRequests), null, mapped);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpGet("mine")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetMyRequests()
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var requests = await _requestService.GetByAthleteIdAsync(athleteId);
            return Ok(await MapRequests(requests));
        }

        // ── Trainer ───────────────────────────────────────────────────────────

        [HttpGet("pending")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetPending()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var requests = await _requestService.GetPendingByTrainerIdAsync(trainerId);
            return Ok(await MapRequests(requests));
        }

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
        [Required]
        [EmailAddress]
        public string TrainerEmail { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Note { get; set; }
    }
}
