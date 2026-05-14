using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/trainingblock")]
    [Authorize]
    public class TrainingBlockController : ControllerBase
    {
        private readonly TrainingBlockService _blockService;
        private readonly UserService _userService;

        public TrainingBlockController(
            TrainingBlockService blockService,
            UserService userService)
        {
            _blockService = blockService;
            _userService = userService;
        }

        private static object Map(TrainingBlock b) => new
        {
            id        = b.Id,
            trainerId = b.TrainerId,
            athleteId = b.AthleteId,
            focus     = b.Focus,
            startDate = b.StartDate,
            endDate   = b.EndDate,
            notes     = b.Notes,
            createdAt = b.CreatedAt
        };

        // Athlete views their own blocks; trainer views blocks for one of their athletes.
        [HttpGet("athlete/{athleteId}")]
        public async Task<IActionResult> GetForAthlete(string athleteId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isTrainer = User.IsInRole("Trainer");

            if (isTrainer)
            {
                var athlete = await _userService.GetByIdAsync(athleteId);
                if (athlete == null || athlete.Role != UserRole.Athlete || athlete.TrainerId != userId)
                    return Forbid();
            }
            else
            {
                if (athleteId != userId) return Forbid();
            }

            var blocks = await _blockService.GetByAthleteAsync(athleteId);
            return Ok(blocks.Select(Map).ToList());
        }

        [HttpPost]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Create([FromBody] TrainingBlockRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (string.IsNullOrEmpty(request.AthleteId))
                return BadRequest(new { message = "AthleteId is required." });

            var athlete = await _userService.GetByIdAsync(request.AthleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete)
                return BadRequest(new { message = "Athlete not found." });
            if (athlete.TrainerId != trainerId)
                return Forbid();

            if (request.EndDate < request.StartDate)
                return BadRequest(new { message = "'endDate' must be on or after 'startDate'." });

            if (await _blockService.HasOverlapAsync(request.AthleteId, request.StartDate, request.EndDate))
                return Conflict(new { message = "Block overlaps an existing block for this athlete." });

            var block = new TrainingBlock
            {
                TrainerId = trainerId,
                AthleteId = request.AthleteId,
                Focus     = request.Focus,
                StartDate = request.StartDate,
                EndDate   = request.EndDate,
                Notes     = request.Notes
            };

            await _blockService.CreateAsync(block);
            return CreatedAtAction(nameof(GetForAthlete), new { athleteId = block.AthleteId }, Map(block));
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Update(string id, [FromBody] TrainingBlockRequest request)
        {
            var existing = await _blockService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != trainerId)
                return Forbid();

            if (request.EndDate < request.StartDate)
                return BadRequest(new { message = "'endDate' must be on or after 'startDate'." });

            if (await _blockService.HasOverlapAsync(
                    existing.AthleteId, request.StartDate, request.EndDate, excludeId: id))
                return Conflict(new { message = "Block overlaps an existing block for this athlete." });

            await _blockService.UpdateAsync(id, request.Focus, request.StartDate, request.EndDate, request.Notes);

            var updated = await _blockService.GetByIdAsync(id);
            return Ok(Map(updated!));
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await _blockService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != trainerId)
                return Forbid();

            await _blockService.DeleteAsync(id);
            return NoContent();
        }
    }

    public class TrainingBlockRequest
    {
        public string? AthleteId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Focus { get; set; } = string.Empty;

        [Required]
        public DateTime StartDate { get; set; }

        [Required]
        public DateTime EndDate { get; set; }

        [MaxLength(1000)]
        public string? Notes { get; set; }
    }
}
