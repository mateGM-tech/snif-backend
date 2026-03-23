using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests.Services;

public class PushNotificationServiceTests
{
    [Fact]
    public async Task SendPushAsync_WithoutDeviceTokens_PersistsNotification()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new SNIFContext(options);
        var service = new PushNotificationService(
            context,
            Mock.Of<ILogger<PushNotificationService>>());

        await service.SendPushAsync(
            "user-1",
            "New Message \ud83d\udcac",
            "hello there",
            new Dictionary<string, string>
            {
                ["type"] = "message",
                ["matchId"] = "match-1"
            });

        var notification = await context.Notifications.SingleAsync();
        notification.UserId.Should().Be("user-1");
        notification.Type.Should().Be("message");
        notification.Title.Should().Be("New Message \ud83d\udcac");
        notification.Body.Should().Be("hello there");
        notification.IsRead.Should().BeFalse();
        notification.Data.Should().Contain("match-1");
    }
}