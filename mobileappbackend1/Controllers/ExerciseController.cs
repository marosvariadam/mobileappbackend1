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
    public class ExerciseController : ControllerBase
    {
        private readonly ExerciseService _exerciseService;

        public ExerciseController(ExerciseService exerciseService)
        {
            _exerciseService = exerciseService;
        }

        // GET /api/exercise?search=squat&muscleGroup=Quads&page=1&pageSize=20
        [HttpGet]
        public async Task<ActionResult<List<Exercise>>> GetAll(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? search = null,
            [FromQuery] string? muscleGroup = null)
        {
            var exercises = await _exerciseService.GetAllAsync(page, pageSize, search, muscleGroup);
            return Ok(exercises);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Exercise>> GetById(string id)
        {
            var exercise = await _exerciseService.GetByIdAsync(id);
            if (exercise == null) return NotFound();
            return Ok(exercise);
        }

        [HttpPost]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Create(Exercise exercise)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            exercise.CreatedByTrainerId = trainerId;
            exercise.Id = null;

            await _exerciseService.CreateAsync(exercise);
            return CreatedAtAction(nameof(GetById), new { id = exercise.Id }, exercise);
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<IActionResult> Update(string id, [FromBody] UpdateExerciseRequest request)
        {
            var existing = await _exerciseService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            if (existing.CreatedByTrainerId != null)
            {
                var currentTrainerId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (existing.CreatedByTrainerId != currentTrainerId)
                    return Forbid();
            }

            await _exerciseService.UpdateAsync(id, request.Name, request.MuscleGroup, request.Description, request.Equipment);
            var updated = await _exerciseService.GetByIdAsync(id);
            return Ok(updated);
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await _exerciseService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            if (existing.CreatedByTrainerId == null)
                return BadRequest(new { message = "Cannot delete system default exercises." });

            var currentTrainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.CreatedByTrainerId != currentTrainerId)
                return Forbid();

            await _exerciseService.RemoveAsync(id);
            return NoContent();
        }
    }

    public class UpdateExerciseRequest
    {
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string MuscleGroup { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(1000)]
        public string? Description { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(200)]
        public string? Equipment { get; set; }
    }
}
