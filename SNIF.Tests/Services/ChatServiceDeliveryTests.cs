using AutoMapper;
using FluentAssertions;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Infrastructure.Repository;
using System.Linq.Expressions;

namespace SNIF.Tests.Services;

public class ChatServiceDeliveryTests
{
    private readonly Mock<IRepository<Message>> _messageRepo = new();
    private readonly Mock<IRepository<MessageReaction>> _reactionRepo = new();
    private readonly Mock<IMapper> _mapper = new();

    private ChatService CreateService() => new(_messageRepo.Object, _reactionRepo.Object, _mapper.Object);

    #region MarkMessagesDeliveredAsync

    [Fact]
    public async Task ConfirmDeliveryAsync_SetsDeliveredAt_ForAuthorizedReceiver()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(
            (Message source) => new MessageDto
            {
                Id = source.Id,
                Content = source.Content,
                SenderId = source.SenderId,
                ReceiverId = source.ReceiverId,
                MatchId = source.MatchId,
                DeliveredAt = source.DeliveredAt,
                CreatedAt = source.CreatedAt
            });

        var service = CreateService();
        var result = await service.ConfirmDeliveryAsync("msg1", "u2");

        result.Should().NotBeNull();
        result!.DeliveredAt.Should().NotBeNull();
        message.DeliveredAt.Should().NotBeNull();
        _messageRepo.Verify(r => r.UpdateAsync(It.Is<Message>(m => m.Id == "msg1" && m.DeliveredAt.HasValue)), Times.Once);
    }

    [Fact]
    public async Task ConfirmDeliveryAsync_WhenReceiverDoesNotMatch_ReturnsNull()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);

        var service = CreateService();
        var result = await service.ConfirmDeliveryAsync("msg1", "u3");

        result.Should().BeNull();
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task MarkMessagesDeliveredAsync_SetsDeliveredAtOnUndeliveredMessages()
    {
        var msg1 = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        var msg2 = new Message
        {
            Id = "msg2", Content = "Hey", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow.AddMinutes(-3)
        };

        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message> { msg1, msg2 });
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);

        var service = CreateService();
        var result = await service.MarkMessagesDeliveredAsync("m1", "u2");

        result.Should().HaveCount(2);
        result.Should().Contain("msg1");
        result.Should().Contain("msg2");
        msg1.DeliveredAt.Should().NotBeNull();
        msg2.DeliveredAt.Should().NotBeNull();
        msg1.UpdatedAt.Should().BeAfter(DateTime.MinValue);
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MarkMessagesDeliveredAsync_NoUndelivered_ReturnsEmptyList()
    {
        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message>());

        var service = CreateService();
        var result = await service.MarkMessagesDeliveredAsync("m1", "u2");

        result.Should().BeEmpty();
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Never);
    }

    #endregion

    #region MarkConversationAsReadAsync

    [Fact]
    public async Task MarkConversationAsReadAsync_SetsIsReadAndReadAtAndDeliveredAt()
    {
        var msg = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message> { msg });
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);

        var service = CreateService();
        var result = await service.MarkConversationAsReadAsync("m1", "u2");

        result.MessageIds.Should().HaveCount(1);
        result.MessageIds.Should().Contain("msg1");
        result.ReadAt.Should().NotBeNull();
        msg.IsRead.Should().BeTrue();
        msg.ReadAt.Should().NotBeNull();
        msg.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkConversationAsReadAsync_PreservesExistingDeliveredAt()
    {
        var existingDelivery = DateTime.UtcNow.AddMinutes(-2);
        var msg = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, DeliveredAt = existingDelivery,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message> { msg });
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);

        var service = CreateService();
        var result = await service.MarkConversationAsReadAsync("m1", "u2");

        result.ReadAt.Should().Be(msg.ReadAt);
        msg.DeliveredAt.Should().Be(existingDelivery);
        msg.ReadAt.Should().NotBeNull();
        msg.IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task MarkConversationAsReadAsync_NoUnread_ReturnsEmptyList()
    {
        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message>());

        var service = CreateService();
        var result = await service.MarkConversationAsReadAsync("m1", "u2");

        result.MessageIds.Should().BeEmpty();
        result.ReadAt.Should().BeNull();
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Never);
    }

    #endregion

    #region MarkAsReadAsync (updated)

    [Fact]
    public async Task MarkAsReadAsync_SetsReadAtAndDeliveredAt()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, CreatedAt = DateTime.UtcNow
        };

        var messageDto = new MessageDto
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true, Status = "read"
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(messageDto);

        var service = CreateService();
        var result = await service.MarkAsReadAsync("msg1", "u2");

        result.Should().NotBeNull();
        message.IsRead.Should().BeTrue();
        message.ReadAt.Should().NotBeNull();
        message.DeliveredAt.Should().NotBeNull();
        message.UpdatedAt.Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task MarkAsReadAsync_PreservesExistingDeliveredAt()
    {
        var existingDelivery = DateTime.UtcNow.AddMinutes(-2);
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, DeliveredAt = existingDelivery,
            CreatedAt = DateTime.UtcNow
        };

        var messageDto = new MessageDto
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true, Status = "read"
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(messageDto);

        var service = CreateService();
        await service.MarkAsReadAsync("msg1", "u2");

        message.DeliveredAt.Should().Be(existingDelivery);
    }

    #endregion

    #region GetMatchIdsWithUndeliveredMessagesAsync

    [Fact]
    public async Task GetMatchIdsWithUndeliveredMessagesAsync_ReturnsDistinctMatchIds()
    {
        var messages = new List<Message>
        {
            new() { Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2", MatchId = "m1", CreatedAt = DateTime.UtcNow },
            new() { Id = "msg2", Content = "Hey", SenderId = "u3", ReceiverId = "u2", MatchId = "m2", CreatedAt = DateTime.UtcNow },
            new() { Id = "msg3", Content = "Ho", SenderId = "u1", ReceiverId = "u2", MatchId = "m1", CreatedAt = DateTime.UtcNow },
        };

        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(messages);

        var service = CreateService();
        var result = await service.GetMatchIdsWithUndeliveredMessagesAsync("u2");

        result.Should().HaveCount(2);
        result.Should().Contain("m1");
        result.Should().Contain("m2");
    }

    [Fact]
    public async Task GetMatchIdsWithUndeliveredMessagesAsync_NoUndelivered_ReturnsEmpty()
    {
        _messageRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<Message, bool>>>()))
            .ReturnsAsync(new List<Message>());

        var service = CreateService();
        var result = await service.GetMatchIdsWithUndeliveredMessagesAsync("u2");

        result.Should().BeEmpty();
    }

    #endregion
}
