using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    [ApiController]
    [Route("api/onboarding-form")]
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

        [HttpGet("responses")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetAllResponses()
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var responses = await _formService.GetAllResponsesForTrainerAsync(trainerId);

            // Enrich with athlete names
            var athleteIds = responses.Select(r => r.AthleteId).Distinct().ToList();
            var athletes = new Dictionary<string, User>();
            foreach (var aid in athleteIds)
            {
                var a = await _userService.GetByIdAsync(aid);
                if (a != null) athletes[aid] = a;
            }

            var mapped = responses.Select(r =>
            {
                athletes.TryGetValue(r.AthleteId, out var athlete);
                return new
                {
                    athleteId   = r.AthleteId,
                    athleteName = athlete != null ? $"{athlete.FirstName} {athlete.LastName}" : (string?)null,
                    answers     = r.Answers,
                    submittedAt = r.SubmittedAt
                };
            });

            return Ok(mapped);
        }

        [HttpGet("responses/{athleteId}")]
        [Authorize(Roles = "Trainer")]
        public async Task<IActionResult> GetAthleteResponse(string athleteId)
        {
            var trainerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            var athlete = await _userService.GetByIdAsync(athleteId);
            if (athlete == null || athlete.TrainerId != trainerId) return Forbid();

            var response = await _formService.GetResponseAsync(athleteId, trainerId);
            if (response == null)
                return NotFound(new { message = "Athlete has not submitted a response yet." });

            return Ok(new
            {
                athleteId   = response.AthleteId,
                athleteName = $"{athlete.FirstName} {athlete.LastName}",
                answers     = response.Answers,
                submittedAt = response.SubmittedAt
            });
        }

        // ── Athlete: fill in the form ─────────────────────────────────────────

        [HttpGet("my-trainer-form")]
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

        [HttpPost("submit")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> Submit(
            [FromBody] SubmitOnboardingResponseRequest request)
        {
            var athleteId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var athlete   = await _userService.GetByIdAsync(athleteId);

            if (string.IsNullOrEmpty(athlete?.TrainerId))
                return BadRequest(new { message = "You are not linked to a trainer." });

            // Auto-detect formId from the athlete's trainer if not provided
            var formId = request.FormId;
            if (string.IsNullOrWhiteSpace(formId))
            {
                var trainerForm = await _formService.GetByTrainerIdAsync(athlete.TrainerId);
                if (trainerForm == null)
                    return NotFound(new { message = "Intake form not found." });
                formId = trainerForm.Id!;
            }
            else
            {
                var form = await _formService.GetByIdAsync(formId);
                if (form == null || form.TrainerId != athlete.TrainerId)
                    return NotFound(new { message = "Intake form not found." });
            }

            var response = await _formService.SubmitResponseAsync(
                athleteId, athlete.TrainerId, formId, request.Answers);

            // Notify the trainer
            await _notificationService.CreateAndSendAsync(
                athlete.TrainerId,
                NotificationType.OnboardingFormSubmitted,
                "Intake Form Completed",
                $"{athlete.FirstName} {athlete.LastName} has submitted their intake questionnaire.",
                response.Id);

            return Ok(response);
        }

        [HttpGet("my-response")]
        [Authorize(Roles = "Athlete")]
        public async Task<IActionResult> GetMyResponse()
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
        public string? FormId { get; set; }

        [Required]
        public List<AnswerEntry> Answers { get; set; } = new();
    }
}
