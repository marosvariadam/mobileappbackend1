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
    public class WorkoutController : ControllerBase
    {
        private readonly WorkoutService _workoutService;
        private readonly UserService _userService;

        public WorkoutController(WorkoutService workoutService, UserService userService)
        {
            _workoutService = workoutService;
            _userService = userService;
        }

        // ── Trainer: write a session ──────────────────────────────────────────

        /// <summary>
        /// Trainer creates a session and assigns it to one of their athletes.
        /// AthleteId must belong to an athlete whose TrainerId matches the caller.
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Create([FromBody] CreateWorkoutRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            // Validate that the athlete exists and belongs to this trainer
            var athlete = await _userService.GetByIdAsync(request.AthleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete)
                return BadRequest(new { message = "Athlete not found." });
            if (athlete.TrainerId != trainerId)
                return Forbid();

            var workout = new Workout
            {
                TrainerId     = trainerId,
                AthleteId     = request.AthleteId,
                Title         = request.Title,
                TrainerNotes  = request.TrainerNotes,
                Difficulty    = request.Difficulty,
                ScheduledDate = request.ScheduledDate,
                Status        = WorkoutStatus.Planned,
                Exercises     = request.Exercises.Select(e => new WorkoutExercise
                {
                    ExerciseId        = e.ExerciseId,
                    Name              = e.Name,
                    Sets              = e.Sets,
                    TargetRepetitions = e.TargetRepetitions,
                    TargetWeightKg    = e.TargetWeightKg,
                    TrainerNotes      = e.Instructions
                }).ToList()
            };

            await _workoutService.CreateAsync(workout);
            return CreatedAtAction(nameof(GetById), new { id = workout.Id }, workout);
        }

        /// <summary>
        /// Trainer edits a session. Only allowed while status is Planned (athlete hasn't started).
        /// </summary>
        [HttpPut("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Update(string id, [FromBody] CreateWorkoutRequest request)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != trainerId)
                return Forbid();

            if (existing.Status != WorkoutStatus.Planned)
                return Conflict(new { message = "Cannot edit a session the athlete has already started." });

            var exercises = request.Exercises.Select(e => new WorkoutExercise
            {
                ExerciseId        = e.ExerciseId,
                Name              = e.Name,
                Sets              = e.Sets,
                TargetRepetitions = e.TargetRepetitions,
                TargetWeightKg    = e.TargetWeightKg,
                TrainerNotes      = e.Instructions
            }).ToList();

            await _workoutService.UpdateAsync(
                id, request.Title, request.TrainerNotes, request.Difficulty,
                request.ScheduledDate, exercises);

            return NoContent();
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.TrainerId != trainerId)
                return Forbid();

            await _workoutService.DeleteAsync(id);
            return NoContent();
        }

        /// <summary>
        /// Trainer reviews all completed sessions (with athlete feedback).
        /// Optional ?athleteId= filter to see one athlete's completed sessions.
        /// </summary>
        [HttpGet("trainer/review")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<Workout>>> GetTrainerReview(
            [FromQuery] string? athleteId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetCompletedByTrainerIdAsync(
                trainerId, athleteId, page, pageSize);
            return Ok(workouts);
        }

        /// <summary>
        /// Trainer sees all sessions they have created (any status).
        /// </summary>
        [HttpGet("trainer/created")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<Workout>>> GetTrainerHistory(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (trainerId == null) return Unauthorized();

            var workouts = await _workoutService.GetByTrainerIdAsync(trainerId, page, pageSize);
            return Ok(workouts);
        }

        /// <summary>
        /// Trainer calendar: all sessions they created within a date window, for any of their athletes.
        /// Example: GET /api/workout/trainer/calendar?from=2025-03-01&to=2025-03-31
        /// </summary>
        [HttpGet("trainer/calendar")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<Workout>>> GetTrainerCalendar(
            [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            if (to < from)
                return BadRequest(new { message = "'to' must be after 'from'." });
            if ((to - from).TotalDays > 365)
                return BadRequest(new { message = "Date range cannot exceed 365 days." });

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetByDateRangeForTrainerAsync(trainerId, from, to);
            return Ok(workouts);
        }

        // ── Athlete: calendar and session lifecycle ───────────────────────────

        /// <summary>
        /// Athlete gets their sessions for a date window — drives the calendar view.
        /// Example: GET /api/workout/calendar?from=2025-03-01&to=2025-03-31
        /// </summary>
        [HttpGet("calendar")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<List<Workout>>> GetCalendar(
            [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            if (to < from)
                return BadRequest(new { message = "'to' must be after 'from'." });
            if ((to - from).TotalDays > 365)
                return BadRequest(new { message = "Date range cannot exceed 365 days." });

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetByDateRangeAsync(athleteId, from, to);
            return Ok(workouts);
        }

        /// <summary>
        /// Athlete gets all their sessions (paged), sorted by scheduled date.
        /// </summary>
        [HttpGet("my-workouts")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<List<Workout>>> GetMyWorkouts(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (athleteId == null) return Unauthorized();

            var workouts = await _workoutService.GetByAthleteIdAsync(athleteId, page, pageSize);
            return Ok(workouts);
        }

        /// <summary>
        /// Athlete starts a session: Planned → InProgress. Records StartedAt.
        /// </summary>
        [HttpPatch("{id}/start")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> Start(string id)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.AthleteId != athleteId)
                return Forbid();

            if (existing.Status != WorkoutStatus.Planned)
                return Conflict(new { message = "Session has already been started or completed." });

            await _workoutService.StartAsync(id);
            return NoContent();
        }

        /// <summary>
        /// Athlete logs actual results for one exercise by its index in the list.
        /// The session must be InProgress.
        /// </summary>
        [HttpPatch("{id}/exercises/{exerciseIndex:int}")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> LogExercise(
            string id, int exerciseIndex, [FromBody] LogExerciseRequest request)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.AthleteId != athleteId)
                return Forbid();

            if (existing.Status != WorkoutStatus.InProgress)
                return Conflict(new { message = "Session must be in progress to log exercises." });

            if (exerciseIndex < 0 || exerciseIndex >= existing.Exercises.Count)
                return BadRequest(new { message = $"Exercise index must be between 0 and {existing.Exercises.Count - 1}." });

            await _workoutService.LogExerciseAsync(
                id, exerciseIndex,
                request.ActualSets, request.ActualRepetitions,
                request.ActualWeightKg, request.AthleteNotes);

            return NoContent();
        }

        /// <summary>
        /// Athlete submits the completed session with optional overall feedback for the trainer.
        /// Transitions InProgress → Completed. Records CompletedAt.
        /// </summary>
        [HttpPatch("{id}/complete")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> Complete(
            string id, [FromBody] CompleteWorkoutRequest request)
        {
            var existing = await _workoutService.GetByIdAsync(id);
            if (existing == null) return NotFound();

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.AthleteId != athleteId)
                return Forbid();

            if (existing.Status != WorkoutStatus.InProgress)
                return Conflict(new { message = "Session must be in progress to submit." });

            await _workoutService.CompleteWithFeedbackAsync(id, request.AthleteFeedback);
            return NoContent();
        }

        // ── Shared ───────────────────────────────────────────────────────────

        [HttpGet("{id}")]
        public async Task<ActionResult<Workout>> GetById(string id)
        {
            var workout = await _workoutService.GetByIdAsync(id);
            if (workout == null) return NotFound();

            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isTrainer = User.IsInRole("Trainer");

            if (!isTrainer && workout.AthleteId != currentUserId)
                return Forbid();

            return Ok(workout);
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    /// <summary>Trainer uses this to create or update a session.</summary>
    public class CreateWorkoutRequest
    {
        /// <summary>Short session title, e.g. "Upper Body Strength".</summary>
        [Required] [MaxLength(300)]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Free-text description / motivational note visible to the athlete before they start.
        /// E.g. "Focus on controlled negatives today. Rest 2 min between sets."
        /// </summary>
        [MaxLength(2000)]
        public string? TrainerNotes { get; set; }

        /// <summary>How demanding this session is intended to be.</summary>
        public DifficultyLevel Difficulty { get; set; } = DifficultyLevel.Moderate;

        [Required]
        public string AthleteId { get; set; } = string.Empty;

        /// <summary>The date this session should appear on in both calendars.</summary>
        [Required]
        public DateTime ScheduledDate { get; set; }

        [Required]
        public List<CreateWorkoutExerciseItem> Exercises { get; set; } = new();
    }

    public class CreateWorkoutExerciseItem
    {
        /// <summary>Id from the exercise catalogue (built-in or trainer's own custom exercise).</summary>
        [Required] public string ExerciseId { get; set; } = string.Empty;

        /// <summary>Display name — populated from the catalogue on the client side.</summary>
        [Required] [MaxLength(200)] public string Name { get; set; } = string.Empty;

        [Range(1, 100)] public int Sets { get; set; }
        [Range(1, 10000)] public int TargetRepetitions { get; set; }
        [Range(0, 10000)] public double TargetWeightKg { get; set; }

        /// <summary>Per-exercise instructions, e.g. "Keep elbows tucked. Pause at bottom."</summary>
        [MaxLength(1000)] public string? Instructions { get; set; }
    }

    /// <summary>Athlete logs actual results for one exercise.</summary>
    public class LogExerciseRequest
    {
        [Range(1, 100)] public int ActualSets { get; set; }
        [Range(1, 10000)] public int ActualRepetitions { get; set; }
        [Range(0, 10000)] public double ActualWeightKg { get; set; }
        [MaxLength(1000)] public string? AthleteNotes { get; set; }
    }

    /// <summary>Athlete submits the completed session with optional feedback for the trainer.</summary>
    public class CompleteWorkoutRequest
    {
        [MaxLength(2000)]
        public string? AthleteFeedback { get; set; }
    }
}
