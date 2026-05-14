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
        private readonly WorkoutService _workoutService;
        private readonly UserService _userService;

        public ExerciseController(
            ExerciseService exerciseService,
            WorkoutService workoutService,
            UserService userService)
        {
            _exerciseService = exerciseService;
            _workoutService = workoutService;
            _userService = userService;
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

        // GET /api/exercise/for-athlete/{athleteId}?search=squat&muscleGroup=Chest
        // Returns all exercises (system + trainer's custom) sorted by how often
        // each exercise was used with the given athlete (most common first).
        [HttpGet("for-athlete/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetForAthlete(
            string athleteId,
            [FromQuery] string? search = null,
            [FromQuery] string? muscleGroup = null)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete || athlete.TrainerId != trainerId)
                return Forbid();

            // Get all exercises visible to this trainer (system defaults + their own custom ones)
            var allExercises = await _exerciseService.GetVisibleToTrainerAsync(trainerId, search, muscleGroup);

            // Count exercise name frequency from all workouts with this athlete
            var workouts = await _workoutService.GetAllByTrainerAndAthleteAsync(trainerId, athleteId);
            var frequencyMap = workouts
                .SelectMany(w => w.Exercises)
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var sorted = allExercises
                .OrderByDescending(e => frequencyMap.GetValueOrDefault(e.Name, 0))
                .ThenBy(e => e.Name)
                .ToList();

            return Ok(sorted);
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
