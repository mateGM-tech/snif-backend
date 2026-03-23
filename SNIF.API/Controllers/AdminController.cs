using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using System.Security.Claims;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/admin")]
    [EnableRateLimiting("global")]
    [Authorize]
    public class AdminController : ControllerBase
    {
        private readonly IAdminService _adminService;

        public AdminController(IAdminService adminService)
        {
            _adminService = adminService;
        }

        private string GetUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User not authenticated.");

        [HttpGet("dashboard")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetDashboard()
        {
            var dashboard = await _adminService.GetDashboardAsync();
            return Ok(dashboard);
        }

        [HttpGet("users")]
        [Authorize(Policy = "RequireStaffRead")]
        public async Task<IActionResult> GetUsers([FromQuery] AdminUserFilterDto filter)
        {
            var users = await _adminService.GetUsersAsync(filter);
            return Ok(users);
        }

        [HttpGet("users/{id}")]
        [Authorize(Policy = "RequireStaffRead")]
        public async Task<IActionResult> GetUserDetail(string id)
        {
            var user = await _adminService.GetUserDetailAsync(id);
            return Ok(user);
        }

        [HttpPost("users/{id}/suspend")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> SuspendUser(string id, [FromBody] SuspendUserDto dto)
        {
            var adminId = GetUserId();
            await _adminService.SuspendUserAsync(id, dto.DurationDays, dto.Reason, adminId);
            return NoContent();
        }

        [HttpPost("users/{id}/ban")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> BanUser(string id, [FromBody] BanUserDto dto)
        {
            var adminId = GetUserId();
            await _adminService.BanUserAsync(id, dto.Reason, adminId);
            return NoContent();
        }

        [HttpPost("users/{id}/unsuspend")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> UnsuspendUser(string id)
        {
            var adminId = GetUserId();
            await _adminService.UnsuspendUserAsync(id, adminId);
            return NoContent();
        }

        [HttpPost("users/{id}/unban")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> UnbanUser(string id)
        {
            var adminId = GetUserId();
            await _adminService.UnbanUserAsync(id, adminId);
            return NoContent();
        }

        [HttpPost("users/{id}/warn")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> WarnUser(string id, [FromBody] WarnUserDto dto)
        {
            var adminId = GetUserId();
            await _adminService.WarnUserAsync(id, adminId, dto.Reason);
            return NoContent();
        }

        [HttpGet("reports")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetReports([FromQuery] ReportFilterDto filter)
        {
            var reports = await _adminService.GetReportsAsync(filter);
            return Ok(reports);
        }

        [HttpPost("reports/{id}/resolve")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> ResolveReport(string id, [FromBody] ResolveReportDto dto)
        {
            var adminId = GetUserId();
            await _adminService.ResolveReportAsync(id, dto.Resolution, dto.Notes, adminId);
            return NoContent();
        }

        [HttpPost("reports/{id}/dismiss")]
        [Authorize(Policy = "RequireModerator")]
        public async Task<IActionResult> DismissReport(string id, [FromBody] DismissReportDto dto)
        {
            var adminId = GetUserId();
            await _adminService.DismissReportAsync(id, dto.Notes, adminId);
            return NoContent();
        }

        [HttpGet("subscriptions/stats")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetSubscriptionStats()
        {
            var stats = await _adminService.GetSubscriptionStatsAsync();
            return Ok(stats);
        }

        [HttpGet("subscriptions")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetSubscriptions([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
        {
            var subscriptions = await _adminService.GetSubscriptionsAsync(page, pageSize);
            return Ok(subscriptions);
        }

        [HttpGet("revenue")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetRevenue()
        {
            var revenue = await _adminService.GetRevenueAsync();
            return Ok(revenue);
        }

        [HttpGet("payments")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetPayments([FromQuery] PaymentFilterDto filter)
        {
            var payments = await _adminService.GetPaymentTransactionsAsync(filter);
            return Ok(payments);
        }

        [HttpGet("stats")]
        [Authorize(Policy = "RequireAdmin")]
        public async Task<IActionResult> GetStats()
        {
            var dashboard = await _adminService.GetDashboardAsync();
            return Ok(dashboard);
        }

        [HttpGet("system/health")]
        [Authorize(Policy = "RequireSuperAdmin")]
        public async Task<IActionResult> GetSystemHealth()
        {
            var health = await _adminService.GetSystemHealthAsync();
            return Ok(health);
        }
    }
}
