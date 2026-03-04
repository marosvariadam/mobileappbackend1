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
    public class NotificationController : ControllerBase
    {
        private readonly NotificationService _notificationService;

        public NotificationController(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// Get the caller's notifications, newest first.
        /// Use this to load missed notifications after reconnecting.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<Notification>>> GetAll(
            [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            return Ok(await _notificationService.GetForUserAsync(userId, page, pageSize));
        }

        /// <summary>Returns the number of unread notifications for the caller.</summary>
        [HttpGet("unread-count")]
        public async Task<IActionResult> GetUnreadCount()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            var count  = await _notificationService.GetUnreadCountAsync(userId);
            return Ok(new { count });
        }

        /// <summary>Mark a single notification as read.</summary>
        [HttpPatch("{id}/read")]
        public async Task<IActionResult> MarkAsRead(string id)
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            await _notificationService.MarkAsReadAsync(userId, id);
            return NoContent();
        }

        /// <summary>Mark all of the caller's notifications as read.</summary>
        [HttpPatch("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
            await _notificationService.MarkAllAsReadAsync(userId);
            return NoContent();
        }
    }
}
