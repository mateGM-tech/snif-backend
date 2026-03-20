using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;
using SNIF.SignalR.Hubs;

namespace SNIF.Tests.Hubs;

public class ChatHubTests
{
    private readonly Mock<IChatService> _chatService = new();
    private readonly Mock<IMatchService> _matchService = new();
    private readonly Mock<ILogger<ChatHub>> _logger = new();
    private readonly Mock<IPushNotificationService> _pushNotifications = new();
    private readonly Mock<HubCallerContext> _callerContext = new();
    private readonly Mock<IHubCallerClients> _clients = new();
    private readonly Mock<IGroupManager> _groups = new();
    private readonly Mock<IClientProxy> _clientProxy = new();

    private SNIFContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SNIFContext(options);
    }

    private ChatHub CreateHub(string userId)
    {
        _callerContext.SetupGet(context => context.UserIdentifier).Returns(userId);
        _callerContext.SetupGet(context => context.ConnectionId).Returns($"connection-{userId}");
        _clients.Setup(clients => clients.Users(It.IsAny<IReadOnlyList<string>>())).Returns(_clientProxy.Object);
        _clients.Setup(clients => clients.User(It.IsAny<string>())).Returns(_clientProxy.Object);
        _clientProxy.Setup(proxy => proxy.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ChatHub(
            _chatService.Object,
            _matchService.Object,
            _logger.Object,
            _pushNotifications.Object,
            CreateInMemoryContext())
        {
            Context = _callerContext.Object,
            Clients = _clients.Object,
            Groups = _groups.Object,
        };
    }

    [Fact]
    public async Task SendMessage_IgnoresClientSuppliedReceiverId_AndUsesDerivedPeer()
    {
        var hub = CreateHub("sender-user");
        var expectedMessage = new MessageDto
        {
            Id = "message-1",
            MatchId = "match-1",
            SenderId = "sender-user",
            ReceiverId = "derived-peer",
            Content = "Hello",
            CreatedAt = DateTime.UtcNow
        };

        _matchService.Setup(service => service.GetPeerUserIdAsync("match-1", "sender-user"))
            .ReturnsAsync("derived-peer");
        _chatService.Setup(service => service.SendMessageAsync(
                It.Is<CreateMessageDto>(dto =>
                    dto.MatchId == "match-1" &&
                    dto.ReceiverId == "derived-peer" &&
                    dto.Content == "Hello"),
                "sender-user"))
            .ReturnsAsync(expectedMessage);
        _pushNotifications.Setup(service => service.SendPushAsync(
                "derived-peer",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        await hub.SendMessage("match-1", "spoofed-user", "Hello");

        _matchService.Verify(service => service.GetPeerUserIdAsync("match-1", "sender-user"), Times.Once);
        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users =>
            users.Count == 2 && users.Contains("sender-user") && users.Contains("derived-peer"))), Times.Once);
        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users => users.Contains("spoofed-user"))), Times.Never);
    }

    [Fact]
    public async Task SendImageMessage_IgnoresClientSuppliedReceiverId_AndUsesDerivedPeer()
    {
        var hub = CreateHub("sender-user");
        var expectedMessage = new MessageDto
        {
            Id = "message-1",
            MatchId = "match-1",
            SenderId = "sender-user",
            ReceiverId = "derived-peer",
            Content = "📷 Photo",
            AttachmentType = "image",
            CreatedAt = DateTime.UtcNow
        };

        _matchService.Setup(service => service.GetPeerUserIdAsync("match-1", "sender-user"))
            .ReturnsAsync("derived-peer");
        _chatService.Setup(service => service.SendImageMessageAsync(
                "match-1",
                "sender-user",
                "derived-peer",
            "https://snif.blob.core.windows.net/chat/photo.png",
                "photo.png",
                42L))
            .ReturnsAsync(expectedMessage);
        _pushNotifications.Setup(service => service.SendPushAsync(
                "derived-peer",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        await hub.SendImageMessage("match-1", "spoofed-user", "https://snif.blob.core.windows.net/chat/photo.png", "photo.png", 42L);

        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users =>
            users.Count == 2 && users.Contains("sender-user") && users.Contains("derived-peer"))), Times.Once);
        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users => users.Contains("spoofed-user"))), Times.Never);
    }

    [Fact]
    public async Task SendReaction_IgnoresClientSuppliedReceiverId_AndUsesMessagePeer()
    {
        var hub = CreateHub("sender-user");
        var reaction = new MessageReactionDto
        {
            Id = "reaction-1",
            UserId = "sender-user",
            Emoji = "👍",
            CreatedAt = DateTime.UtcNow
        };

        _chatService.Setup(service => service.GetMessageByIdAsync("message-1"))
            .ReturnsAsync(new MessageDto
            {
                Id = "message-1",
                MatchId = "match-1",
                SenderId = "sender-user",
                ReceiverId = "derived-peer",
                Content = "Hello",
                CreatedAt = DateTime.UtcNow
            });
        _chatService.Setup(service => service.AddReactionAsync("message-1", "sender-user", "👍"))
            .ReturnsAsync(reaction);

        await hub.SendReaction("message-1", "👍", "spoofed-user");

        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users =>
            users.Count == 2 && users.Contains("sender-user") && users.Contains("derived-peer"))), Times.Once);
        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users => users.Contains("spoofed-user"))), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_IgnoresClientSuppliedReceiverId_AndUsesMessagePeer()
    {
        var hub = CreateHub("sender-user");

        _chatService.Setup(service => service.GetMessageByIdAsync("message-1"))
            .ReturnsAsync(new MessageDto
            {
                Id = "message-1",
                MatchId = "match-1",
                SenderId = "sender-user",
                ReceiverId = "derived-peer",
                Content = "Hello",
                CreatedAt = DateTime.UtcNow
            });
        _chatService.Setup(service => service.RemoveReactionAsync("message-1", "sender-user", "👍"))
            .ReturnsAsync(true);

        await hub.RemoveReaction("message-1", "👍", "spoofed-user");

        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users =>
            users.Count == 2 && users.Contains("sender-user") && users.Contains("derived-peer"))), Times.Once);
        _clients.Verify(clients => clients.Users(It.Is<IReadOnlyList<string>>(users => users.Contains("spoofed-user"))), Times.Never);
    }

    [Fact]
    public async Task SendReaction_WhenCallerIsNotMessageParticipant_ThrowsHubException()
    {
        var hub = CreateHub("intruder-user");

        _chatService.Setup(service => service.GetMessageByIdAsync("message-1"))
            .ReturnsAsync(new MessageDto
            {
                Id = "message-1",
                MatchId = "match-1",
                SenderId = "sender-user",
                ReceiverId = "derived-peer",
                Content = "Hello",
                CreatedAt = DateTime.UtcNow
            });

        var action = () => hub.SendReaction("message-1", "👍", "spoofed-user");

        await action.Should().ThrowAsync<HubException>()
            .WithMessage("User not authorized for this message");
        _chatService.Verify(service => service.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task MarkMessageAsRead_WhenCallerIsNotReceiver_ThrowsHubException()
    {
        var hub = CreateHub("sender-user");

        _chatService.Setup(service => service.MarkAsReadAsync("message-1", "sender-user"))
            .ThrowsAsync(new UnauthorizedAccessException("Only the receiver can mark a message as read"));

        var action = () => hub.MarkMessageAsRead("message-1");

        await action.Should().ThrowAsync<HubException>()
            .WithMessage("Only the receiver can mark a message as read");
        _clientProxy.Verify(proxy => proxy.SendCoreAsync("MessageRead", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}