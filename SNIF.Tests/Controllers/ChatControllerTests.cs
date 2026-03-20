using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using SNIF.API.Extensions;
using SNIF.API.Controllers;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.SignalR.Hubs;
using System.Security.Claims;

namespace SNIF.Tests.Controllers;

public class ChatControllerTests
{
    private readonly Mock<IChatService> _chatService = new();
    private readonly Mock<IMatchService> _matchService = new();
    private readonly Mock<IMediaStorageService> _mediaStorage = new();
    private readonly Mock<IHubContext<ChatHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly Mock<IPushNotificationService> _pushNotificationService = new();

    private ChatController CreateController(string userId)
    {
        _hubContext.SetupGet(context => context.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(clients => clients.Users(It.IsAny<IReadOnlyList<string>>())).Returns(_clientProxy.Object);

        var controller = new ChatController(
            _chatService.Object,
            _matchService.Object,
            _mediaStorage.Object,
            _hubContext.Object,
            _pushNotificationService.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId)
                }, "TestAuth"))
            }
        };

        return controller;
    }

    [Fact]
    public async Task SendImageMessage_DerivesReceiverFromMatch_InsteadOfClientInput()
    {
        var controller = CreateController("sender-user");
        var imageBytes = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(imageBytes);
        var image = new FormFile(stream, 0, imageBytes.Length, "image", "photo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var expectedMessage = new MessageDto
        {
            Id = "message-1",
            MatchId = "match-1",
            SenderId = "sender-user",
            ReceiverId = "derived-peer",
            AttachmentUrl = "https://cdn.example.com/chat/photo.png",
            AttachmentType = "image",
            CreatedAt = DateTime.UtcNow
        };

        _matchService.Setup(service => service.GetPeerUserIdAsync("match-1", "sender-user"))
            .ReturnsAsync("derived-peer");
        _mediaStorage.Setup(storage => storage.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), "image/png"))
            .ReturnsAsync("https://cdn.example.com/chat/photo.png");
        _chatService.Setup(service => service.SendImageMessageAsync(
                "match-1",
                "sender-user",
                "derived-peer",
                "https://cdn.example.com/chat/photo.png",
                "photo.png",
                imageBytes.Length))
            .ReturnsAsync(expectedMessage);
        _clientProxy.Setup(proxy => proxy.SendCoreAsync("ReceiveMessage", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _pushNotificationService.Setup(service => service.SendPushAsync(
                "derived-peer",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);

        var result = await controller.SendImageMessage("match-1", image);

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().BeEquivalentTo(expectedMessage);

        _matchService.Verify(service => service.GetPeerUserIdAsync("match-1", "sender-user"), Times.Once);
        _chatService.Verify(service => service.SendImageMessageAsync(
            "match-1",
            "sender-user",
            "derived-peer",
            It.IsAny<string>(),
            "photo.png",
            imageBytes.Length), Times.Once);
    }

    [Fact]
    public async Task AddReaction_WhenCallerIsNotMessageParticipant_ReturnsUnauthorized()
    {
        var controller = CreateController("intruder-user");
        _chatService.Setup(service => service.AddReactionAsync("message-1", "intruder-user", "👍"))
            .ThrowsAsync(new UnauthorizedAccessException("User not authorized for this message"));

        var result = await controller.AddReaction("message-1", new AddReactionDto { Emoji = "👍" });

        var unauthorized = result.Result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeEquivalentTo(new ErrorResponse { Message = "User not authorized for this message" });
    }

    [Fact]
    public async Task RemoveReaction_WhenCallerIsNotMessageParticipant_ReturnsUnauthorized()
    {
        var controller = CreateController("intruder-user");
        _chatService.Setup(service => service.RemoveReactionAsync("message-1", "intruder-user", "👍"))
            .ThrowsAsync(new UnauthorizedAccessException("User not authorized for this message"));

        var result = await controller.RemoveReaction("message-1", "👍");

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeEquivalentTo(new ErrorResponse { Message = "User not authorized for this message" });
    }

    [Fact]
    public async Task MarkAsRead_WhenCallerIsNotReceiver_ReturnsUnauthorized()
    {
        var controller = CreateController("sender-user");
        _chatService.Setup(service => service.MarkAsReadAsync("message-1", "sender-user"))
            .ThrowsAsync(new UnauthorizedAccessException("Only the receiver can mark a message as read"));

        var result = await controller.MarkAsRead("message-1");

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        unauthorized.Value.Should().BeEquivalentTo(new ErrorResponse { Message = "Only the receiver can mark a message as read" });
    }
}