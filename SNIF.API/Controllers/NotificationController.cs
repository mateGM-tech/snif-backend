using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.Core.Utilities;
using SNIF.Infrastructure.Data;
using System.Security.Claims;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/notifications")]
    [Authorize]
    [EnableRateLimiting("global")]
    public class NotificationController : ControllerBase
    {
        private readonly IPushNotificationService _pushNotificationService;
        private readonly SNIFContext _context;

        public NotificationController(IPushNotificationService pushNotificationService, SNIFContext context)
        {
            _pushNotificationService = pushNotificationService;
            _context = context;
        }

        private string GetUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User not authenticated.");

        [HttpPost("register-device")]
        public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceDto dto)
        {
            var userId = GetUserId();
            await _pushNotificationService.RegisterDeviceAsync(userId, dto.Token, dto.Platform);
            return Ok();
        }

        [HttpDelete("unregister-device")]
        public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceDto dto)
        {
            var userId = GetUserId();
            await _pushNotificationService.UnregisterDeviceAsync(userId, dto.Token);
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> GetNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var userId = GetUserId();
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = _context.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.CreatedAt);

            var totalCount = await query.CountAsync();
            var items = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(n => new
                {
                    n.Id,
                    n.Type,
                    n.Title,
                    n.Body,
                    n.Data,
                    n.IsRead,
                    n.CreatedAt
                })
                .ToListAsync();

            return Ok(new { items, totalCount, page, pageSize });
        }

        [HttpPut("{id}/read")]
        public async Task<IActionResult> MarkAsRead(string id)
        {
            var userId = GetUserId();
            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

            if (notification == null)
                return NotFound();

            notification.IsRead = true;
            notification.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPut("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = GetUserId();
            await _context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.IsRead, true)
                    .SetProperty(n => n.UpdatedAt, DateTime.UtcNow));

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteNotification(string id)
        {
            var userId = GetUserId();
            var notification = await _context.Notifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);

            if (notification == null)
                return NotFound();

            _context.Notifications.Remove(notification);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPut("read-by-match/{matchId}")]
        public async Task<IActionResult> MarkMessageNotificationsAsRead(string matchId)
        {
            var userId = GetUserId();
            await MarkMatchNotificationsAsReadAsync(userId, matchId);

            return NoContent();
        }

        private async Task MarkMatchNotificationsAsReadAsync(string userId, string matchId)
        {
            var candidates = await _context.Notifications
                .Where(n => n.UserId == userId && n.Type == "message" && !n.IsRead)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var hasChanges = false;

            foreach (var notification in candidates)
            {
                if (!NotificationDataParser.MatchesMatchId(notification.Data, matchId))
                {
                    continue;
                }

                notification.IsRead = true;
                notification.UpdatedAt = now;
                hasChanges = true;
            }

            if (hasChanges)
            {
                await _context.SaveChangesAsync();
            }
        }
    }
}
