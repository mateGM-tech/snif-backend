using AutoMapper;
using FluentAssertions;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Mappings;

namespace SNIF.Tests.Services;

public class MessageMappingProfileTests
{
    private readonly IMapper _mapper;

    public MessageMappingProfileTests()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<MessageMappingProfile>());
        _mapper = config.CreateMapper();
    }

    [Fact]
    public void Status_WhenReadAtSet_ReturnsRead()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true, ReadAt = DateTime.UtcNow,
            DeliveredAt = DateTime.UtcNow.AddMinutes(-1), CreatedAt = DateTime.UtcNow
        };

        var dto = _mapper.Map<MessageDto>(message);

        dto.Status.Should().Be("read");
        dto.ReadAt.Should().NotBeNull();
        dto.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public void Status_WhenDeliveredAtSetButNotRead_ReturnsDelivered()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, DeliveredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        var dto = _mapper.Map<MessageDto>(message);

        dto.Status.Should().Be("delivered");
        dto.DeliveredAt.Should().NotBeNull();
        dto.ReadAt.Should().BeNull();
    }

    [Fact]
    public void Status_WhenNeitherDeliveredNorRead_ReturnsSent()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = false, CreatedAt = DateTime.UtcNow
        };

        var dto = _mapper.Map<MessageDto>(message);

        dto.Status.Should().Be("sent");
        dto.DeliveredAt.Should().BeNull();
        dto.ReadAt.Should().BeNull();
    }

    [Fact]
    public void Status_BackwardCompat_IsReadTrueWithReadAt_ReturnsRead()
    {
        var message = new Message
        {
            Id = "msg1", Content = "Hi", SenderId = "u1", ReceiverId = "u2",
            MatchId = "m1", IsRead = true, ReadAt = DateTime.UtcNow,
            DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };

        var dto = _mapper.Map<MessageDto>(message);

        dto.Status.Should().Be("read");
        dto.IsRead.Should().BeTrue();
    }
}
