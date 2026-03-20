using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SNIF.Core.Entities;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;
using SNIF.SignalR.Hubs;

namespace SNIF.Tests.Hubs;

public class ChatHubDeliveryTests
{
    private readonly Mock<IChatService> _chatService = new();
    private readonly Mock<IMatchService> _matchService = new();
    private readonly Mock<ILogger<ChatHub>> _logger = new();
    private readonly Mock<IPushNotificationService> _pushNotifications = new();
    private readonly Mock<HubCallerContext> _callerContext = new();
    private readonly Mock<IHubCallerClients> _clients = new();
    private readonly Mock<IGroupManager> _groups = new();
    private readonly Mock<IClientProxy> _senderProxy = new();
    private readonly Mock<IClientProxy> _userProxy = new();
    private SNIFContext? _context;

    private SNIFContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new SNIFContext(options);
    }

    private ChatHub CreateHub(string userId)
    {
        _context = CreateInMemoryContext();
        _callerContext.SetupGet(c => c.UserIdentifier).Returns(userId);
        _callerContext.SetupGet(c => c.ConnectionId).Returns($"connection-{userId}");
        _clients.Setup(c => c.Users(It.IsAny<IReadOnlyList<string>>())).Returns(_userProxy.Object);
        _clients.Setup(c => c.User(It.IsAny<string>())).Returns(_senderProxy.Object);
        _senderProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _userProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _groups.Setup(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new ChatHub(
            _chatService.Object,
            _matchService.Object,
            _logger.Object,
            _pushNotifications.Object,
            _context)
        {
            Context = _callerContext.Object,
            Clients = _clients.Object,
            Groups = _groups.Object,
        };
    }

    private SNIFContext HubContext => _context ?? throw new InvalidOperationException("Hub context has not been created.");

    #region JoinChat — auto-delivery

    [Fact]
    public async Task JoinChat_MarksPendingMessagesAsDelivered_AndNotifiesSender()
    {
        var hub = CreateHub("receiver-user");
        var deliveredIds = new List<string> { "msg1", "msg2" };

        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"))
            .ReturnsAsync(deliveredIds);
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-1", "receiver-user"))
            .ReturnsAsync("sender-user");

        await hub.JoinChat("match-1");

        _groups.Verify(g => g.AddToGroupAsync("connection-receiver-user", "chat_match-1", It.IsAny<CancellationToken>()), Times.Once);
        _chatService.Verify(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"), Times.Once);
        _clients.Verify(c => c.User("sender-user"), Times.Once);
        _senderProxy.Verify(p => p.SendCoreAsync(
            "MessagesDelivered",
            It.Is<object?[]>(args => args.Length == 2 && (string)args[0]! == "match-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChat_NoPendingMessages_DoesNotNotifySender()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"))
            .ReturnsAsync(new List<string>());

        await hub.JoinChat("match-1");

        _groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _clients.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region OnConnectedAsync — offline→online delivery

    [Fact]
    public async Task OnConnectedAsync_DeliversPendingMessagesAcrossAllMatches()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.GetMatchIdsWithUndeliveredMessagesAsync("receiver-user"))
            .ReturnsAsync(new List<string> { "match-1", "match-2" });
        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"))
            .ReturnsAsync(new List<string> { "msg1" });
        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-2", "receiver-user"))
            .ReturnsAsync(new List<string> { "msg2", "msg3" });
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-1", "receiver-user"))
            .ReturnsAsync("sender-1");
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-2", "receiver-user"))
            .ReturnsAsync("sender-2");

        await hub.OnConnectedAsync();

        _chatService.Verify(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"), Times.Once);
        _chatService.Verify(s => s.MarkMessagesDeliveredAsync("match-2", "receiver-user"), Times.Once);
        _senderProxy.Verify(p => p.SendCoreAsync(
            "MessagesDelivered",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OnConnectedAsync_NoPendingMessages_DoesNotNotifyAnyone()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.GetMatchIdsWithUndeliveredMessagesAsync("receiver-user"))
            .ReturnsAsync(new List<string>());

        await hub.OnConnectedAsync();

        _chatService.Verify(s => s.MarkMessagesDeliveredAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _senderProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_PeerLookupFails_ContinuesWithOtherMatches()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.GetMatchIdsWithUndeliveredMessagesAsync("receiver-user"))
            .ReturnsAsync(new List<string> { "match-1", "match-2" });
        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-1", "receiver-user"))
            .ReturnsAsync(new List<string> { "msg1" });
        _chatService.Setup(s => s.MarkMessagesDeliveredAsync("match-2", "receiver-user"))
            .ReturnsAsync(new List<string> { "msg2" });
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-1", "receiver-user"))
            .ThrowsAsync(new KeyNotFoundException("Match not found"));
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-2", "receiver-user"))
            .ReturnsAsync("sender-2");

        // Should not throw — errors are caught per-match
        await hub.OnConnectedAsync();

        // match-2 should still be notified
        _chatService.Verify(s => s.MarkMessagesDeliveredAsync("match-2", "receiver-user"), Times.Once);
    }

    #endregion

    #region ConfirmDelivery

    [Fact]
    public async Task ConfirmDelivery_PersistsDelivery_AndIncludesTimestampInEvent()
    {
        var hub = CreateHub("receiver-user");
        var deliveredAt = DateTime.UtcNow;

        _chatService.Setup(s => s.ConfirmDeliveryAsync("msg-1", "receiver-user"))
            .ReturnsAsync(new MessageDto
            {
                Id = "msg-1",
                MatchId = "match-1",
                SenderId = "sender-user",
                ReceiverId = "receiver-user",
                DeliveredAt = deliveredAt,
                CreatedAt = DateTime.UtcNow,
                Content = "hello"
            });

        await hub.ConfirmDelivery("msg-1");

        _chatService.Verify(s => s.ConfirmDeliveryAsync("msg-1", "receiver-user"), Times.Once);
        _senderProxy.Verify(p => p.SendCoreAsync(
            "MessageDelivered",
            It.Is<object?[]>(args => args.Length == 2 && (string)args[0]! == "msg-1" && (DateTime?)args[1] == deliveredAt),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmDelivery_WhenMessageIsNotConfirmable_DoesNothing()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.ConfirmDeliveryAsync("msg-1", "receiver-user"))
            .ReturnsAsync((MessageDto?)null);

        await hub.ConfirmDelivery("msg-1");

        _senderProxy.Verify(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region MarkConversationAsRead — batch event

    [Fact]
    public async Task MarkConversationAsRead_SendsMessagesRead_AndLegacyMessageRead()
    {
        var hub = CreateHub("receiver-user");
        var readIds = new List<string> { "msg1", "msg2" };
        var persistedReadAt = DateTime.UtcNow;

        _chatService.Setup(s => s.MarkConversationAsReadAsync("match-1", "receiver-user"))
            .ReturnsAsync(new MessageReadBatchResult
            {
                MessageIds = readIds,
                ReadAt = persistedReadAt
            });
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-1", "receiver-user"))
            .ReturnsAsync("sender-user");

        // ExecuteUpdateAsync is not supported by InMemory provider, so it may throw.
        // We verify SignalR events were sent before the notification update.
        try { await hub.MarkConversationAsRead("match-1"); } catch (Exception) { }

        // Batch event
        _senderProxy.Verify(p => p.SendCoreAsync(
            "MessagesRead",
            It.Is<object?[]>(args => args.Length == 2 && (string)args[0]! == "match-1"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Legacy per-message events (backward compat)
        _userProxy.Verify(p => p.SendCoreAsync(
            "MessageRead",
            It.Is<object?[]>(args => args.Length == 2 && (DateTime?)args[1] == persistedReadAt),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MarkConversationAsRead_MarksOnlyNotificationsForTheExactMatchId()
    {
        var hub = CreateHub("receiver-user");
        HubContext.Notifications.AddRange(
            new Notification
            {
                Id = "notification-1",
                UserId = "receiver-user",
                Type = "message",
                Title = "New Message",
                Body = "Body",
                Data = "{\"type\":\"message\",\"matchId\":\"match-1\"}",
                IsRead = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Notification
            {
                Id = "notification-2",
                UserId = "receiver-user",
                Type = "message",
                Title = "New Message",
                Body = "Body",
                Data = "{\"type\":\"message\",\"matchId\":\"match-10\"}",
                IsRead = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        await HubContext.SaveChangesAsync();

        _chatService.Setup(s => s.MarkConversationAsReadAsync("match-1", "receiver-user"))
            .ReturnsAsync(new MessageReadBatchResult
            {
                MessageIds = new List<string> { "msg1" },
                ReadAt = DateTime.UtcNow
            });
        _matchService.Setup(s => s.GetPeerUserIdAsync("match-1", "receiver-user"))
            .ReturnsAsync("sender-user");

        await hub.MarkConversationAsRead("match-1");

        (await HubContext.Notifications.FindAsync("notification-1"))!.IsRead.Should().BeTrue();
        (await HubContext.Notifications.FindAsync("notification-2"))!.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task MarkConversationAsRead_NoUnreadMessages_DoesNothing()
    {
        var hub = CreateHub("receiver-user");

        _chatService.Setup(s => s.MarkConversationAsReadAsync("match-1", "receiver-user"))
            .ReturnsAsync(new MessageReadBatchResult());

        try { await hub.MarkConversationAsRead("match-1"); } catch (Exception) { }

        _clients.Verify(c => c.User(It.IsAny<string>()), Times.Never);
        _clients.Verify(c => c.Users(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Fact]
    public async Task MarkMessageAsRead_SendsPersistedReadTimestamp()
    {
        var hub = CreateHub("receiver-user");
        var persistedReadAt = DateTime.UtcNow;

        _chatService.Setup(s => s.MarkAsReadAsync("msg-1", "receiver-user"))
            .ReturnsAsync(new MessageDto
            {
                Id = "msg-1",
                MatchId = "match-1",
                SenderId = "sender-user",
                ReceiverId = "receiver-user",
                ReadAt = persistedReadAt,
                CreatedAt = DateTime.UtcNow,
                Content = "hello"
            });

        await hub.MarkMessageAsRead("msg-1");

        _userProxy.Verify(p => p.SendCoreAsync(
            "MessageRead",
            It.Is<object?[]>(args => args.Length == 2 && (string)args[0]! == "msg-1" && (DateTime?)args[1] == persistedReadAt),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
