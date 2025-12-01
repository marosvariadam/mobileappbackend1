using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using mobileappbackend1.Models;
using mobileappbackend1.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class WorkoutController : ControllerBase
    {
        private readonly WorkoutService _workoutService;

        public WorkoutController(WorkoutService workoutService)
        {
            _workoutService = workoutService;
        }


        [HttpPost]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Create(Workout workout)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            workout.TrainerId = trainerId!;

            workout.Id = null;

            await _workoutService.CreateAsync(workout);
            return CreatedAtAction(nameof(GetById), new { id = workout.Id }, workout);
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Workout>> GetById(string id)
        {
            var workout = await _workoutService.GetByIdAsync(id);
            if (workout == null) return NotFound();
            return Ok(workout);
        }

        [HttpGet("my-workouts")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<List<Workout>>> GetMyWorkouts()
        {
            // Extract Athlete ID from Token
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (athleteId == null) return Unauthorized();

            var workouts = await _workoutService.GetByAthleteIdAsync(athleteId);
            return Ok(workouts);
        }

        [HttpGet("trainer/created")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<Workout>>> GetTrainerHistory()
        {
            // Extract Trainer ID from Token
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (trainerId == null) return Unauthorized();

            var workouts = await _workoutService.GetByTrainerIdAsync(trainerId);
            return Ok(workouts);
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Update(string id, Workout workout)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var currentTrainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != currentTrainerId)
            {
                return Forbid(); 
            }

            await _workoutService.UpdateAsync(id, workout);
            return NoContent();
        }

        [HttpPatch("{id}/complete")]
        public async Task<IActionResult> MarkComplete(string id, [FromBody] bool isCompleted)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            await _workoutService.ToggleCompletionAsync(id, isCompleted);
            return NoContent();
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var currentTrainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != currentTrainerId)
            {
                return Forbid();
            }

            await _workoutService.DeleteAsync(id);
            return NoContent();
        }

    }
}
