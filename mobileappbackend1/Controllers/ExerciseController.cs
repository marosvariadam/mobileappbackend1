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
}
