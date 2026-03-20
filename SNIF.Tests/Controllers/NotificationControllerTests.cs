using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SNIF.API.Controllers;
using SNIF.Core.Entities;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;
using System.Security.Claims;

namespace SNIF.Tests.Controllers;

public class NotificationControllerTests
{
    [Fact]
    public async Task MarkMessageNotificationsAsRead_UsesExactJsonMatchId()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new SNIFContext(options);
        context.Notifications.AddRange(
            new Notification
            {
                Id = "notification-1",
                UserId = "user-1",
                Type = "message",
                Title = "Match 1",
                Body = "Body",
                Data = "{\"type\":\"message\",\"matchId\":\"match-1\"}",
                IsRead = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Notification
            {
                Id = "notification-2",
                UserId = "user-1",
                Type = "message",
                Title = "Match 10",
                Body = "Body",
                Data = "{\"type\":\"message\",\"matchId\":\"match-10\"}",
                IsRead = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        await context.SaveChangesAsync();

        var controller = new NotificationController(Mock.Of<IPushNotificationService>(), context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                        "TestAuth"))
                }
            }
        };

        var result = await controller.MarkMessageNotificationsAsRead("match-1");

        result.Should().BeOfType<NoContentResult>();
        (await context.Notifications.FindAsync("notification-1"))!.IsRead.Should().BeTrue();
        (await context.Notifications.FindAsync("notification-2"))!.IsRead.Should().BeFalse();
    }
}