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
    public class OnboardingFormController : ControllerBase
    {
        private readonly OnboardingFormService _formService;
        private readonly NotificationService   _notificationService;
        private readonly UserService           _userService;

        public OnboardingFormController(
            OnboardingFormService formService,
            NotificationService   notificationService,
            UserService           userService)
        {
            _formService         = formService;
            _notificationService = notificationService;
            _userService         = userService;
        }

        // ── Trainer: manage their intake form ─────────────────────────────────

        /// <summary>
        /// Create or replace the trainer's intake form.
        /// Each trainer has exactly one active form; submitting again overwrites it.
        ///
        /// Typical questions:
        ///   - "Describe your sport history and experience level." (Text)
        ///   - "Have you had any injuries in the past 2 years?" (Text)
        ///   - "What is your primary goal?" (MultipleChoice: Weight loss / Muscle gain / Performance / General fitness)
        ///   - "How would you rate your current fitness level?" (Scale, 1–10)
        ///   - "How many days per week can you train?" (MultipleChoice: 2 / 3 / 4 / 5+)
        /// </summary>
        [HttpPut]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<OnboardingForm>> Upsert(
            [FromBody] UpsertOnboardingFormRequest request)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var form = await _formService.UpsertAsync(
                trainerId, request.Title, request.Description, request.Questions);
            return Ok(form);
        }

        /// <summary>Trainer retrieves their own intake form.</summary>
        [HttpGet("mine")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<OnboardingForm>> GetMine()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var form = await _formService.GetByTrainerIdAsync(trainerId);
            if (form == null)
                return NotFound(new { message = "You haven't created an intake form yet." });
            return Ok(form);
        }

        /// <summary>Trainer views all submitted athlete responses.</summary>
        [HttpGet("responses")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<List<OnboardingResponse>>> GetAllResponses()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            return Ok(await _formService.GetAllResponsesForTrainerAsync(trainerId));
        }

        /// <summary>
        /// Trainer views a specific athlete's onboarding response.
        /// Verifies the athlete belongs to this trainer.
        /// </summary>
        [HttpGet("responses/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<ActionResult<OnboardingResponse>> GetAthleteResponse(string athleteId)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.TrainerId != trainerId) return Forbid();

            var response = await _formService.GetResponseAsync(athleteId, trainerId);
            if (response == null)
                return NotFound(new { message = "Athlete has not submitted a response yet." });

            return Ok(response);
        }

        // ── Athlete: fill in the form ─────────────────────────────────────────

        /// <summary>
        /// Athlete retrieves their trainer's intake form.
        /// The athlete must be linked to a trainer first (via an accepted join request).
        /// </summary>
        [HttpGet("my-form")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<OnboardingForm>> GetMyForm()
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete   = await _userService.GetByIdAsync(athleteId);

            if (string.IsNullOrEmpty(athlete?.TrainerId))
                return NotFound(new { message = "You are not linked to a trainer yet." });

            var form = await _formService.GetByTrainerIdAsync(athlete.TrainerId);
            if (form == null)
                return NotFound(new { message = "Your trainer has not created an intake form yet." });

            return Ok(form);
        }

        /// <summary>
        /// Athlete submits their answers.
        /// Re-submitting updates the existing response.
        /// The trainer is notified in real-time on submission.
        /// </summary>
        [HttpPost("submit")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<OnboardingResponse>> Submit(
            [FromBody] SubmitOnboardingResponseRequest request)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete   = await _userService.GetByIdAsync(athleteId);

            if (string.IsNullOrEmpty(athlete?.TrainerId))
                return BadRequest(new { message = "You are not linked to a trainer." });

            var form = await _formService.GetByIdAsync(request.FormId);
            if (form == null || form.TrainerId != athlete.TrainerId)
                return NotFound(new { message = "Intake form not found." });

            var response = await _formService.SubmitResponseAsync(
                athleteId, athlete.TrainerId, request.FormId, request.Answers);

            // Notify the trainer
            await _notificationService.CreateAndSendAsync(
                athlete.TrainerId,
                NotificationType.OnboardingFormSubmitted,
                "Intake Form Completed",
                $"{athlete.FirstName} {athlete.LastName} has submitted their intake questionnaire.",
                response.Id);

            return Ok(response);
        }

        /// <summary>Athlete retrieves their own previously submitted response.</summary>
        [HttpGet("my-response")]
        [Authorize(Roles = "Athlete")]
        public async Task<ActionResult<OnboardingResponse>> GetMyResponse()
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete   = await _userService.GetByIdAsync(athleteId);

            if (string.IsNullOrEmpty(athlete?.TrainerId))
                return NotFound(new { message = "You are not linked to a trainer." });

            var response = await _formService.GetResponseAsync(athleteId, athlete.TrainerId);
            if (response == null)
                return NotFound(new { message = "You have not submitted the intake form yet." });

            return Ok(response);
        }
    }

    // ── Request DTOs ──────────────────────────────────────────────────────────

    public class UpsertOnboardingFormRequest
    {
        [Required] [MaxLength(200)]
        public string Title { get; set; } = "Athlete Intake Questionnaire";

        [MaxLength(1000)]
        public string? Description { get; set; }

        [Required]
        public List<OnboardingQuestion> Questions { get; set; } = new();
    }

    public class SubmitOnboardingResponseRequest
    {
        [Required]
        public string FormId { get; set; } = string.Empty;

        [Required]
        public List<AnswerEntry> Answers { get; set; } = new();
    }
}
