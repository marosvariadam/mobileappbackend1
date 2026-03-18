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
    public class MessageController : ControllerBase
    {
        private readonly MessageService _messageService;
        private readonly UserService _userService;

        public MessageController(MessageService messageService, UserService userService)
        {
            _messageService = messageService;
            _userService    = userService;
        }

        // ── GET conversations ─────────────────────────────────────────────────

        [HttpGet("conversations")]
        public async Task<IActionResult> GetConversations()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var conversations = await _messageService.GetConversationsAsync(userId);

            var userIds = conversations.Select(c => c.OtherUserId).Distinct().ToList();
            var users = new Dictionary<string, User>();
            foreach (var uid in userIds)
            {
                var u = await _userService.GetByIdAsync(uid);
                if (u != null) users[uid] = u;
            }

            var response = conversations.Select(c =>
            {
                users.TryGetValue(c.OtherUserId, out var other);
                return new
                {
                    partnerId        = c.OtherUserId,
                    partnerName      = other != null ? $"{other.FirstName} {other.LastName}" : "Unknown",
                    partnerAvatarUrl = (string?)null,
                    lastMessage      = c.LastMessageContent,
                    lastMessageAt    = c.LastSentAt,
                    unreadCount      = c.UnreadCount
                };
            });

            return Ok(response);
        }

        // ── GET history ───────────────────────────────────────────────────────

        [HttpGet("{otherId}")]
        public async Task<ActionResult<List<Message>>> GetHistory(
            string otherId,
            [FromQuery] int page     = 1,
            [FromQuery] int pageSize = 50)
        {
            var other = await _userService.GetByIdAsync(otherId);
            if (other == null) return NotFound(new { message = "User not found." });

            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var messages = await _messageService.GetConversationAsync(userId, otherId, page, pageSize);
            return Ok(messages);
        }

        // ── POST send ─────────────────────────────────────────────────────────

        [HttpPost("{recipientId}")]
        public async Task<IActionResult> Send(
            string recipientId,
            [FromBody] SendMessageRequest request)
        {
            var senderId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;

            if (senderId == recipientId)
                return BadRequest(new { message = "You cannot send a message to yourself." });

            if (!await _messageService.IsRelationshipValidAsync(senderId, recipientId))
                return Forbid();

            var message = await _messageService.SendAsync(senderId, recipientId, request.Content);
            return CreatedAtAction(nameof(GetHistory), new { otherId = recipientId }, message);
        }

        // ── PATCH mark as read ────────────────────────────────────────────────

        [HttpPatch("{otherId}/read")]
        public async Task<IActionResult> MarkAsRead(string otherId)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            await _messageService.MarkConversationAsReadAsync(userId, otherId);
            return NoContent();
        }
    }

    public class SendMessageRequest
    {
        [Required]
        [MinLength(1)]
        [MaxLength(2000)]
        public string Content { get; set; } = string.Empty;
    }
}
