using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

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

        [HttpGet]
        public async Task<ActionResult<List<Exercise>>> GetAll()
        {
            var exercises = await _exerciseService.GetAllAsync();
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

            
            var currentTrainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            
            if (existing.CreatedByTrainerId == null)
            {
                return BadRequest("Cannot delete system default exercises.");
            }

            if (existing.CreatedByTrainerId != currentTrainerId)
            {
                return Forbid();
            }

            await _exerciseService.RemoveAsync(id);
            return NoContent();
        }
    }

}
