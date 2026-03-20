using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SNIF.Core.DTOs
{
    public record MessageDto
    {
        public string Id { get; init; } = null!;
        public string Content { get; init; } = null!;
        public string SenderId { get; init; } = null!;
        public string ReceiverId { get; init; } = null!;
        public string MatchId { get; init; } = null!;
        public bool IsRead { get; init; }
        public string Status { get; init; } = "sent"; // Derived from persisted timestamps: sent -> delivered -> read
        public DateTime? DeliveredAt { get; init; }
        public DateTime? ReadAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? AttachmentUrl { get; init; }
        public string? AttachmentType { get; init; }
        public string? AttachmentFileName { get; init; }
        public long? AttachmentSizeBytes { get; init; }
        public List<MessageReactionDto> Reactions { get; init; } = new();
    }

    public record MessageReadBatchResult
    {
        public IReadOnlyList<string> MessageIds { get; init; } = Array.Empty<string>();
        public DateTime? ReadAt { get; init; }
    }

    public record CreateMessageDto
    {
        public required string Content { get; init; }
        public required string ReceiverId { get; init; }
        public required string MatchId { get; init; }
    }

    public record ChatSummaryDto
    {
        public string MatchId { get; init; } = null!;
        public string PartnerId { get; init; } = null!;
        public string PartnerName { get; init; } = null!;
        public string? PartnerProfilePicture { get; init; }
        public string? PartnerPetName { get; init; }
        public string? PartnerPetId { get; init; }
        public MessageDto? LastMessage { get; init; }
        public int UnreadCount { get; init; }
    }

    public record AddReactionDto
    {
        public required string Emoji { get; init; }
    }

    public record MessageReactionDto
    {
        public string Id { get; init; } = null!;
        public string UserId { get; init; } = null!;
        public string Emoji { get; init; } = null!;
        public DateTime CreatedAt { get; init; }
    }
}
