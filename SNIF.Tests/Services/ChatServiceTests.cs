using AutoMapper;
using FluentAssertions;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Specifications;
using SNIF.Infrastructure.Repository;

namespace SNIF.Tests.Services;

public class ChatServiceTests
{
    private readonly Mock<IRepository<Message>> _messageRepo = new();
    private readonly Mock<IRepository<MessageReaction>> _reactionRepo = new();
    private readonly Mock<IMapper> _mapper = new();

    private ChatService CreateService() => new(_messageRepo.Object, _reactionRepo.Object, _mapper.Object);

    [Fact]
    public async Task SendMessageAsync_CreatesMessage()
    {
        var createDto = new CreateMessageDto
        {
            Content = "Hello!",
            ReceiverId = "user2",
            MatchId = "match1"
        };

        var message = new Message
        {
            Id = "msg1",
            Content = "Hello!",
            SenderId = "user1",
            ReceiverId = "user2",
            MatchId = "match1",
            CreatedAt = DateTime.UtcNow
        };

        var messageDto = new MessageDto
        {
            Id = "msg1",
            Content = "Hello!",
            SenderId = "user1",
            ReceiverId = "user2",
            MatchId = "match1",
            CreatedAt = DateTime.UtcNow
        };

        _mapper.Setup(m => m.Map<Message>(createDto)).Returns(message);
        _messageRepo.Setup(r => r.AddAsync(It.IsAny<Message>())).ReturnsAsync(message);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(messageDto);

        var service = CreateService();
        var result = await service.SendMessageAsync(createDto, "user1");

        result.Should().NotBeNull();
        result.Content.Should().Be("Hello!");
        result.SenderId.Should().Be("user1");
        _messageRepo.Verify(r => r.AddAsync(It.IsAny<Message>()), Times.Once);
    }

    [Fact]
    public async Task GetMatchMessagesAsync_ReturnsMessages()
    {
        var messages = new List<Message>
        {
            new() { Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2", MatchId = "m1", CreatedAt = DateTime.UtcNow },
            new() { Id = "msg2", Content = "Hey", SenderId = "u2", ReceiverId = "u1", MatchId = "m1", CreatedAt = DateTime.UtcNow }
        };

        var messageDtos = new List<MessageDto>
        {
            new() { Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2", MatchId = "m1" },
            new() { Id = "msg2", Content = "Hey", SenderId = "u2", ReceiverId = "u1", MatchId = "m1" }
        };

        _messageRepo.Setup(r => r.FindBySpecificationAsync(It.IsAny<IQuerySpecification<Message>>()))
            .ReturnsAsync(messages);
        _mapper.Setup(m => m.Map<IEnumerable<MessageDto>>(messages)).Returns(messageDtos);

        var service = CreateService();
        var result = await service.GetMatchMessagesAsync("m1");

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task MarkAsReadAsync_SetsIsReadTrue()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, CreatedAt = DateTime.UtcNow
        };

        var messageDto = new MessageDto
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _messageRepo.Setup(r => r.UpdateAsync(It.IsAny<Message>())).Returns(Task.CompletedTask);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(messageDto);

        var service = CreateService();
        var result = await service.MarkAsReadAsync("msg1", "u2");

        result.Should().NotBeNull();
        result!.IsRead.Should().BeTrue();
        _messageRepo.Verify(r => r.UpdateAsync(It.Is<Message>(m => m.IsRead)), Times.Once);
    }

    [Fact]
    public async Task MarkAsReadAsync_AlreadyRead_ReturnsNull()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true, CreatedAt = DateTime.UtcNow
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);

        var service = CreateService();
        var result = await service.MarkAsReadAsync("msg1", "u2");

        result.Should().BeNull();
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task MarkAsReadAsync_WhenCallerIsNotReceiver_Throws()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(new Message
        {
            Id = "msg1",
            Content = "Hi",
            SenderId = "u1",
            ReceiverId = "u2",
            MatchId = "m1",
            CreatedAt = DateTime.UtcNow
        });

        var service = CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.MarkAsReadAsync("msg1", "u1"));
        _messageRepo.Verify(r => r.UpdateAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task GetUserChatsAsync_ReturnsGroupedChats()
    {
        var sender = new User { Id = "u1", Name = "Alice", UserName = "alice" };
        var receiver = new User { Id = "u2", Name = "Bob", UserName = "bob" };

        var messages = new List<Message>
        {
            new()
            {
                Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
                MatchId = "m1", IsRead = false, Sender = sender, Receiver = receiver,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new()
            {
                Id = "msg2", Content = "Hey", SenderId = "u2", ReceiverId = "u1",
                MatchId = "m1", IsRead = false, Sender = receiver, Receiver = sender,
                CreatedAt = DateTime.UtcNow
            }
        };

        _messageRepo.Setup(r => r.FindBySpecificationAsync(It.IsAny<IQuerySpecification<Message>>()))
            .ReturnsAsync(messages);
        _mapper.Setup(m => m.Map<MessageDto>(It.IsAny<Message>())).Returns(
            (Message m) => new MessageDto
            {
                Id = m.Id, Content = m.Content, SenderId = m.SenderId,
                ReceiverId = m.ReceiverId, MatchId = m.MatchId, CreatedAt = m.CreatedAt
            });

        var service = CreateService();
        var result = (await service.GetUserChatsAsync("u1")).ToList();

        result.Should().HaveCount(1);
        result[0].MatchId.Should().Be("m1");
        result[0].UnreadCount.Should().Be(1); // msg2 is unread and received by u1
    }

    [Fact]
    public async Task AddReactionAsync_MessageNotFound_Throws()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg-missing")).ReturnsAsync((Message?)null);

        var service = CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.AddReactionAsync("msg-missing", "u1", "👍"));
    }

    [Fact]
    public async Task AddReactionAsync_WhenUserIsNotMessageParticipant_Throws()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(new Message
        {
            Id = "msg1",
            Content = "Hi",
            SenderId = "owner-1",
            ReceiverId = "owner-2",
            MatchId = "m1",
            CreatedAt = DateTime.UtcNow
        });

        var service = CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.AddReactionAsync("msg1", "intruder", "👍"));
        _reactionRepo.Verify(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()), Times.Never);
    }

    [Fact]
    public async Task AddReactionAsync_NewReaction_PersistsAndReturnsDto()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow
        };

        var expectedDto = new MessageReactionDto
        {
            Id = "r1", UserId = "u1", Emoji = "👍", CreatedAt = DateTime.UtcNow
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _reactionRepo.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()))
            .ReturnsAsync(new List<MessageReaction>());
        _reactionRepo.Setup(r => r.AddAsync(It.IsAny<MessageReaction>()))
            .ReturnsAsync((MessageReaction r) => r);
        _mapper.Setup(m => m.Map<MessageReactionDto>(It.IsAny<MessageReaction>())).Returns(expectedDto);

        var service = CreateService();
        var result = await service.AddReactionAsync("msg1", "u1", "👍");

        result.Should().NotBeNull();
        result.Emoji.Should().Be("👍");
        _reactionRepo.Verify(r => r.AddAsync(It.IsAny<MessageReaction>()), Times.Once);
    }

    [Fact]
    public async Task AddReactionAsync_AlreadyExists_ReturnsExistingWithoutSaving()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", CreatedAt = DateTime.UtcNow
        };

        var existing = new MessageReaction
        {
            Id = "r1", MessageId = "msg1", UserId = "u1", Emoji = "👍",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        var expectedDto = new MessageReactionDto
        {
            Id = "r1", UserId = "u1", Emoji = "👍", CreatedAt = existing.CreatedAt
        };

        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(message);
        _reactionRepo.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()))
            .ReturnsAsync(new List<MessageReaction> { existing });
        _mapper.Setup(m => m.Map<MessageReactionDto>(existing)).Returns(expectedDto);

        var service = CreateService();
        var result = await service.AddReactionAsync("msg1", "u1", "👍");

        result.Should().NotBeNull();
        result.Id.Should().Be("r1");
        _reactionRepo.Verify(r => r.AddAsync(It.IsAny<MessageReaction>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReactionAsync_Exists_DeletesAndReturnsTrue()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(new Message
        {
            Id = "msg1",
            Content = "Hi",
            SenderId = "u1",
            ReceiverId = "u2",
            MatchId = "m1",
            CreatedAt = DateTime.UtcNow
        });

        var existing = new MessageReaction
        {
            Id = "r1", MessageId = "msg1", UserId = "u1", Emoji = "👍",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };

        _reactionRepo.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()))
            .ReturnsAsync(new List<MessageReaction> { existing });
        _reactionRepo.Setup(r => r.DeleteAsync(existing)).Returns(Task.CompletedTask);

        var service = CreateService();
        var result = await service.RemoveReactionAsync("msg1", "u1", "👍");

        result.Should().BeTrue();
        _reactionRepo.Verify(r => r.DeleteAsync(existing), Times.Once);
    }

    [Fact]
    public async Task RemoveReactionAsync_NotFound_ReturnsFalse()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(new Message
        {
            Id = "msg1",
            Content = "Hi",
            SenderId = "u1",
            ReceiverId = "u2",
            MatchId = "m1",
            CreatedAt = DateTime.UtcNow
        });

        _reactionRepo.Setup(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()))
            .ReturnsAsync(new List<MessageReaction>());

        var service = CreateService();
        var result = await service.RemoveReactionAsync("msg1", "u1", "👍");

        result.Should().BeFalse();
        _reactionRepo.Verify(r => r.DeleteAsync(It.IsAny<MessageReaction>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReactionAsync_WhenUserIsNotMessageParticipant_Throws()
    {
        _messageRepo.Setup(r => r.GetByIdAsync("msg1")).ReturnsAsync(new Message
        {
            Id = "msg1",
            Content = "Hi",
            SenderId = "owner-1",
            ReceiverId = "owner-2",
            MatchId = "m1",
            CreatedAt = DateTime.UtcNow
        });

        var service = CreateService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.RemoveReactionAsync("msg1", "intruder", "👍"));
        _reactionRepo.Verify(r => r.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<MessageReaction, bool>>>()), Times.Never);
    }
}
