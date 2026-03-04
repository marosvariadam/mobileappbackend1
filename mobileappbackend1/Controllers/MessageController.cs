using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using mobileappbackend1.Models;
using mobileappbackend1.Services;

namespace mobileappbackend1.Controllers
{
    /// <summary>
    /// REST API for messaging.
    ///
    /// Endpoint summary:
    ///   GET  /api/message/conversations          — list conversations with last msg + unread count
    ///   GET  /api/message/{otherId}              — paged history with one user
    ///   POST /api/message/{recipientId}          — send a message (REST fallback for SignalR)
    ///   PATCH /api/message/{otherId}/read        — mark all messages from that user as read
    ///
    /// Authorization model:
    ///   • All endpoints require a valid JWT.
    ///   • Conversation ownership is enforced by building ConversationId from the JWT sub-claim,
    ///     never from a client-supplied value.
    ///   • Sending requires an active trainer-athlete relationship.
    ///   • Reading history requires the other user to exist; the query itself is scoped to the
    ///     caller's ConversationId so cross-conversation access is structurally impossible.
    /// </summary>
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

            // Enrich with the other participant's display name in a single pass
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
                    c.OtherUserId,
                    OtherUserName        = other != null ? $"{other.FirstName} {other.LastName}" : "Unknown",
                    c.LastMessageContent,
                    c.LastSentAt,
                    c.UnreadCount
                };
            });

            return Ok(response);
        }

        // ── GET history ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns paged message history between the caller and another user, newest first.
        /// Access is implicitly scoped to the caller: ConversationId is built server-side from
        /// the JWT sub-claim, making cross-conversation access structurally impossible.
        /// </summary>
        [HttpGet("{otherId}")]
        public async Task<ActionResult<List<Message>>> GetHistory(
            string otherId,
            [FromQuery] int page     = 1,
            [FromQuery] int pageSize = 50)
        {
            // Verify the other user exists to avoid silent empty results on typos
            var other = await _userService.GetByIdAsync(otherId);
            if (other == null) return NotFound(new { message = "User not found." });

            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var messages = await _messageService.GetConversationAsync(userId, otherId, page, pageSize);
            return Ok(messages);
        }

        // ── POST send (REST fallback) ─────────────────────────────────────────

        /// <summary>
        /// REST fallback for clients that cannot use the SignalR hub (e.g. background jobs).
        /// The real-time push still happens via the hub when the recipient is online;
        /// this endpoint only handles persistence.
        /// </summary>
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

        /// <summary>
        /// Marks every message from otherId that was sent TO the caller as read.
        /// The RecipientId = currentUserId filter means users can only mark their own inbox.
        /// </summary>
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
