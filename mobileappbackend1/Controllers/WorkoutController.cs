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
        private readonly NotificationService _notificationService;

        public WorkoutController(
            WorkoutService workoutService,
            UserService userService,
            NotificationService notificationService)
        {
            _workoutService = workoutService;
            _userService = userService;
            _notificationService = notificationService;
        }

        // ── Response mapping ────────────────────────────────────────────────────

        private static object MapExercise(WorkoutExercise e, int index) => new
        {
            exerciseId     = e.ExerciseId,
            name           = e.Name,
            index          = index,
            sets           = e.Sets,
            targetReps     = e.TargetRepetitions,
            targetWeightKg = e.TargetWeightKg,
            instructions   = e.TrainerNotes,
            equipmentType  = e.EquipmentType,
            actualSets     = e.ActualSets,
            actualReps     = e.ActualRepetitions,
            actualWeightKg = e.ActualWeightKg,
            exerciseNotes  = e.AthleteNotes
        };

        private static object MapWorkout(Workout w, User? trainer, User? athlete) => new
        {
            id                = w.Id,
            title             = w.Title,
            description       = w.TrainerNotes,
            scheduledDate     = w.ScheduledDate,
            athleteId         = w.AthleteId,
            coachId           = w.TrainerId,
            difficulty        = w.Difficulty.ToString().ToLower(),
            status            = w.Status.ToString().ToLower(),
            trainerName       = trainer != null ? $"{trainer.FirstName} {trainer.LastName}" : (string?)null,
            notes             = w.TrainerNotes,
            athleteName       = athlete != null ? $"{athlete.FirstName} {athlete.LastName}" : (string?)null,
            athleteFeedback   = w.AthleteFeedback,
            estimatedDuration = (string?)null,
            kcal              = (string?)null,
            exercises         = w.Exercises.Select((e, i) => MapExercise(e, i)).ToList()
        };

        private async Task<object> MapWorkoutAsync(Workout w)
        {
            var trainer = await _userService.GetByIdAsync(w.TrainerId);
            var athlete = await _userService.GetByIdAsync(w.AthleteId);
            return MapWorkout(w, trainer, athlete);
        }

        private async Task<List<object>> MapWorkoutsAsync(List<Workout> workouts)
        {
            var userIds = workouts
                .SelectMany(w => new[] { w.TrainerId, w.AthleteId })
                .Distinct()
                .ToList();

            var users = new Dictionary<string, User>();
            foreach (var uid in userIds)
            {
                var u = await _userService.GetByIdAsync(uid);
                if (u != null) users[uid] = u;
            }

            return workouts.Select(w =>
            {
                users.TryGetValue(w.TrainerId, out var trainer);
                users.TryGetValue(w.AthleteId, out var athlete);
                return MapWorkout(w, trainer, athlete);
            }).ToList();
        }

        // ── Trainer: create a session ───────────────────────────────────────────

        [HttpPost]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> Create([FromBody] CreateWorkoutRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (string.IsNullOrEmpty(request.AthleteId))
                return BadRequest(new { message = "AthleteId is required." });

            var athlete = await _userService.GetByIdAsync(request.AthleteId);
            if (athlete == null || athlete.Role != UserRole.Athlete)
                return BadRequest(new { message = "Athlete not found." });
            if (athlete.TrainerId != trainerId)
                return Forbid();

            if (!Enum.TryParse<DifficultyLevel>(request.Difficulty, true, out var difficulty))
                return BadRequest(new { message = "Invalid difficulty. Use: easy, moderate, hard, intense." });

            var workout = new Workout
            {
                TrainerId     = trainerId,
                AthleteId     = request.AthleteId,
                Title         = request.Title,
                TrainerNotes  = request.Notes,
                Difficulty    = difficulty,
                ScheduledDate = request.ScheduledDate,
                Status        = WorkoutStatus.Planned,
                Exercises     = request.Exercises.Select(e => new WorkoutExercise
                {
                    ExerciseId        = e.ExerciseId ?? string.Empty,
                    Name              = e.Name,
                    Sets              = e.Sets,
                    TargetRepetitions = e.TargetReps,
                    TargetWeightKg    = e.TargetWeightKg,
                    TrainerNotes      = e.Instructions,
                    EquipmentType     = e.EquipmentType
                }).ToList()
            };

            await _workoutService.CreateAsync(workout);

            // Notify the athlete
            var trainer = await _userService.GetByIdAsync(trainerId);
            var trainerName = trainer != null ? $"{trainer.FirstName} {trainer.LastName}" : "Your trainer";
            await _notificationService.CreateAndSendAsync(
                request.AthleteId,
                NotificationType.WorkoutAssigned,
                "New Workout Assigned",
                $"{trainerName} has assigned you a new workout: {workout.Title}",
                workout.Id);

            var response = MapWorkout(workout, trainer, athlete);
            return CreatedAtAction(nameof(GetById), new { id = workout.Id }, response);
        }

        // ── Trainer: edit a session ─────────────────────────────────────────────

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

            if (!Enum.TryParse<DifficultyLevel>(request.Difficulty, true, out var difficulty))
                return BadRequest(new { message = "Invalid difficulty. Use: easy, moderate, hard, intense." });

            var exercises = request.Exercises.Select(e => new WorkoutExercise
            {
                ExerciseId        = e.ExerciseId ?? string.Empty,
                Name              = e.Name,
                Sets              = e.Sets,
                TargetRepetitions = e.TargetReps,
                TargetWeightKg    = e.TargetWeightKg,
                TrainerNotes      = e.Instructions,
                EquipmentType     = e.EquipmentType
            }).ToList();

            await _workoutService.UpdateAsync(
                id, request.Title, request.Notes, difficulty,
                request.ScheduledDate, exercises);

            var updated = await _workoutService.GetByIdAsync(id);
            return Ok(await MapWorkoutAsync(updated!));
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

        // ── Trainer: review completed sessions ──────────────────────────────────

        [HttpGet("trainer/review/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetTrainerReview(
            string athleteId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetCompletedByTrainerIdAsync(
                trainerId, athleteId, page, pageSize);
            return Ok(await MapWorkoutsAsync(workouts));
        }

        // ── Trainer: all created sessions ───────────────────────────────────────

        [HttpGet("trainer/created")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetTrainerHistory(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (trainerId == null) return Unauthorized();

            var workouts = await _workoutService.GetByTrainerIdAsync(trainerId, page, pageSize);
            return Ok(await MapWorkoutsAsync(workouts));
        }

        // ── Trainer: calendar ───────────────────────────────────────────────────

        [HttpGet("trainer/calendar")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetTrainerCalendar(
            [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            if (to < from)
                return BadRequest(new { message = "'to' must be after 'from'." });
            if ((to - from).TotalDays > 365)
                return BadRequest(new { message = "Date range cannot exceed 365 days." });

            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetByDateRangeForTrainerAsync(trainerId, from, to);
            return Ok(await MapWorkoutsAsync(workouts));
        }

        // ── Athlete: calendar ───────────────────────────────────────────────────

        [HttpGet("my-workouts/calendar")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetCalendar(
            [FromQuery] DateTime from, [FromQuery] DateTime to)
        {
            if (to < from)
                return BadRequest(new { message = "'to' must be after 'from'." });
            if ((to - from).TotalDays > 365)
                return BadRequest(new { message = "Date range cannot exceed 365 days." });

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var workouts = await _workoutService.GetByDateRangeAsync(athleteId, from, to);
            return Ok(await MapWorkoutsAsync(workouts));
        }

        // ── Athlete: all sessions ───────────────────────────────────────────────

        [HttpGet("my-workouts")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetMyWorkouts(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (athleteId == null) return Unauthorized();

            var workouts = await _workoutService.GetByAthleteIdAsync(athleteId, page, pageSize);
            return Ok(await MapWorkoutsAsync(workouts));
        }

        // ── Athlete: start session ──────────────────────────────────────────────

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

            var updated = await _workoutService.GetByIdAsync(id);
            return Ok(await MapWorkoutAsync(updated!));
        }

        // ── Athlete: log exercise ───────────────────────────────────────────────

        [HttpPatch("{workoutId}/exercise/{index:int}")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> LogExercise(
            string workoutId, int index, [FromBody] LogExerciseRequest request)
        {
            var existing = await _workoutService.GetByIdAsync(workoutId);
            if (existing == null) return NotFound();

            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (existing.AthleteId != athleteId)
                return Forbid();

            if (existing.Status != WorkoutStatus.InProgress)
                return Conflict(new { message = "Session must be in progress to log exercises." });

            if (index < 0 || index >= existing.Exercises.Count)
                return BadRequest(new { message = $"Exercise index must be between 0 and {existing.Exercises.Count - 1}." });

            await _workoutService.LogExerciseAsync(
                workoutId, index,
                request.ActualSets, request.ActualReps,
                request.ActualWeightKg, request.ExerciseNotes);

            return NoContent();
        }

        // ── Athlete: complete session ───────────────────────────────────────────

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

            await _workoutService.CompleteWithFeedbackAsync(id, request.Feedback);

            var updated = await _workoutService.GetByIdAsync(id);
            return Ok(await MapWorkoutAsync(updated!));
        }

        // ── Shared: get by id ───────────────────────────────────────────────────

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(string id)
        {
            var workout = await _workoutService.GetByIdAsync(id);
            if (workout == null) return NotFound();

            var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var isTrainer = User.IsInRole("Trainer");

            if (!isTrainer && workout.AthleteId != currentUserId)
                return Forbid();

            return Ok(await MapWorkoutAsync(workout));
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    public class CreateWorkoutRequest
    {
        [Required] [MaxLength(300)]
        public string Title { get; set; } = string.Empty;

        public string? AthleteId { get; set; }

        [Required]
        public DateTime ScheduledDate { get; set; }

        public string Difficulty { get; set; } = "moderate";

        [MaxLength(2000)]
        public string? Notes { get; set; }

        [Required]
        public List<CreateWorkoutExerciseItem> Exercises { get; set; } = new();
    }

    public class CreateWorkoutExerciseItem
    {
        public string? ExerciseId { get; set; }

        [Required] [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        public int Index { get; set; }

        [Range(1, 100)]
        public int Sets { get; set; }

        [Range(1, 10000)]
        public int TargetReps { get; set; }

        [Range(0, 10000)]
        public double TargetWeightKg { get; set; }

        [MaxLength(1000)]
        public string? Instructions { get; set; }

        [MaxLength(200)]
        public string? EquipmentType { get; set; }
    }

    public class LogExerciseRequest
    {
        [Range(1, 100)] public int ActualSets { get; set; }
        [Range(1, 10000)] public int ActualReps { get; set; }
        [Range(0, 10000)] public double ActualWeightKg { get; set; }
        [MaxLength(1000)] public string? ExerciseNotes { get; set; }
    }

    public class CompleteWorkoutRequest
    {
        [MaxLength(2000)]
        public string? Feedback { get; set; }
    }
}
