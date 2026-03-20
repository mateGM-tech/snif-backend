using SNIF.Core.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SNIF.Core.Interfaces
{
    public interface IChatService
    {
        Task<MessageDto> SendMessageAsync(CreateMessageDto messageDto, string senderId);
        Task<IEnumerable<MessageDto>> GetMatchMessagesAsync(string matchId);
        Task<MessageDto?> GetMessageByIdAsync(string messageId);
        Task<MessageDto?> MarkAsReadAsync(string messageId, string receiverUserId);
        Task<MessageDto?> ConfirmDeliveryAsync(string messageId, string receiverUserId);
        Task<List<string>> MarkMessagesDeliveredAsync(string matchId, string receiverUserId);
        Task<MessageReadBatchResult> MarkConversationAsReadAsync(string matchId, string receiverUserId);
        Task<List<string>> GetMatchIdsWithUndeliveredMessagesAsync(string receiverUserId);
        Task<IEnumerable<ChatSummaryDto>> GetUserChatsAsync(string userId);
        Task<MessageReactionDto> AddReactionAsync(string messageId, string userId, string emoji);
        Task<bool> RemoveReactionAsync(string messageId, string userId, string emoji);
        Task<MessageDto> SendImageMessageAsync(string matchId, string senderId, string receiverId, string attachmentUrl, string fileName, long sizeBytes);
    }
}
