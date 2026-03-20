using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.Core.Utilities;
using SNIF.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SNIF.SignalR.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly IChatService _chatService;
        private readonly IMatchService _matchService;
        private readonly ILogger<ChatHub> _logger;
        private readonly IPushNotificationService _pushNotificationService;
        private readonly SNIFContext _context;

        public ChatHub(IChatService chatService, IMatchService matchService, ILogger<ChatHub> logger, IPushNotificationService pushNotificationService, SNIFContext context)
        {
            _chatService = chatService;
            _matchService = matchService;
            _logger = logger;
            _pushNotificationService = pushNotificationService;
            _context = context;
        }

        private async Task<string> ResolvePeerUserIdAsync(string matchId, string userId)
        {
            try
            {
                return await _matchService.GetPeerUserIdAsync(matchId, userId);
            }
            catch (KeyNotFoundException)
            {
                throw new HubException("Match not found");
            }
            catch (UnauthorizedAccessException)
            {
                throw new HubException("User not authorized for this match");
            }
        }

        private async Task<string> ResolveMessagePeerUserIdAsync(string messageId, string userId)
        {
            var message = await _chatService.GetMessageByIdAsync(messageId)
                ?? throw new HubException("Message not found");

            if (message.SenderId == userId)
            {
                return message.ReceiverId;
            }

            if (message.ReceiverId == userId)
            {
                return message.SenderId;
            }

            throw new HubException("User not authorized for this message");
        }

        public async Task SendMessage(string matchId, string receiverId, string content)
        {
            var senderId = Context.UserIdentifier!;
            _ = receiverId;
            var resolvedReceiverId = await ResolvePeerUserIdAsync(matchId, senderId);
            var sanitizedContent = WebUtility.HtmlEncode(content);
            var message = await _chatService.SendMessageAsync(new CreateMessageDto
            {
                Content = sanitizedContent,
                ReceiverId = resolvedReceiverId,
                MatchId = matchId
            }, senderId);

            await Clients.Users(new[] { senderId, resolvedReceiverId })
                .SendAsync("ReceiveMessage", message);

            // Confirm sent status to sender
            await Clients.User(senderId)
                .SendAsync("MessageSent", message.Id);

            // Send push notification for offline delivery; mobile client deduplicates in foreground
            var pushBody = sanitizedContent.Length > 100 ? sanitizedContent[..100] + "..." : sanitizedContent;
            await _pushNotificationService.SendPushAsync(
                resolvedReceiverId,
                "New Message \ud83d\udcac",
                pushBody,
                new Dictionary<string, string> { ["type"] = "message", ["matchId"] = matchId });
        }

        public async Task SendReaction(string messageId, string emoji, string receiverId)
        {
            var userId = Context.UserIdentifier!;
            _ = receiverId;

            if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 8)
            {
                throw new HubException("Invalid reaction.");
            }
            emoji = WebUtility.HtmlEncode(emoji);

            try
            {
                var resolvedReceiverId = await ResolveMessagePeerUserIdAsync(messageId, userId);
                var reaction = await _chatService.AddReactionAsync(messageId, userId, emoji);

                await Clients.Users(new[] { userId, resolvedReceiverId })
                    .SendAsync("ReceiveReaction", new { messageId, reaction });
            }
            catch (HubException)
            {
                throw;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogWarning(ex, "SendReaction failed: message {MessageId} not found", messageId);
                throw new HubException("Message not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendReaction failed for message {MessageId} by user {UserId}", messageId, userId);
                throw new HubException("Failed to save reaction");
            }
        }

        public async Task RemoveReaction(string messageId, string emoji, string receiverId)
        {
            var userId = Context.UserIdentifier!;
            _ = receiverId;
            try
            {
                var resolvedReceiverId = await ResolveMessagePeerUserIdAsync(messageId, userId);
                var removed = await _chatService.RemoveReactionAsync(messageId, userId, emoji);

                if (removed)
                {
                    await Clients.Users(new[] { userId, resolvedReceiverId })
                        .SendAsync("ReactionRemoved", new { messageId, userId, emoji });
                }
            }
            catch (HubException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RemoveReaction failed for message {MessageId} by user {UserId}", messageId, userId);
                throw new HubException("Failed to remove reaction");
            }
        }

        public async Task SendImageMessage(string matchId, string receiverId, string attachmentUrl, string fileName, long sizeBytes)
        {
            var senderId = Context.UserIdentifier!;
            _ = receiverId;
            var resolvedReceiverId = await ResolvePeerUserIdAsync(matchId, senderId);

            if (!Uri.TryCreate(attachmentUrl, UriKind.Absolute, out var uri) ||
                !uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
            {
                throw new HubException("Invalid attachment URL.");
            }
            fileName = WebUtility.HtmlEncode(fileName);

            var message = await _chatService.SendImageMessageAsync(
                matchId, senderId, resolvedReceiverId, attachmentUrl, fileName, sizeBytes);

            await Clients.Users(new[] { senderId, resolvedReceiverId })
                .SendAsync("ReceiveMessage", message);

            // Confirm sent status to sender
            await Clients.User(senderId)
                .SendAsync("MessageSent", message.Id);

            await _pushNotificationService.SendPushAsync(
                resolvedReceiverId,
                "New Message \ud83d\udcac",
                "📷 Photo",
                new Dictionary<string, string> { ["type"] = "message", ["matchId"] = matchId });
        }

        public async Task ConfirmDelivery(string messageId)
        {
            var userId = Context.UserIdentifier!;
            var message = await _chatService.ConfirmDeliveryAsync(messageId, userId);
            if (message == null) return;

            await Clients.User(message.SenderId)
                .SendAsync("MessageDelivered", message.Id, message.DeliveredAt);
        }

        public async Task MarkMessageAsRead(string messageId)
        {
            var userId = Context.UserIdentifier!;
            try
            {
                var message = await _chatService.MarkAsReadAsync(messageId, userId);

                if (message == null)
                {
                    return;
                }

                await Clients.Users(new[] { userId, message.SenderId })
                    .SendAsync("MessageRead", message.Id, message.ReadAt);

                await MarkMatchNotificationsAsReadAsync(userId, message.MatchId);
            }
            catch (KeyNotFoundException)
            {
                throw new HubException("Message not found");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new HubException(ex.Message);
            }
        }

        public async Task MarkConversationAsRead(string matchId)
        {
            var userId = Context.UserIdentifier!;

            var readResult = await _chatService.MarkConversationAsReadAsync(matchId, userId);

            if (readResult.MessageIds.Count == 0) return;

            var senderId = await ResolvePeerUserIdAsync(matchId, userId);
            var readAt = readResult.ReadAt;
            await Clients.User(senderId)
                .SendAsync("MessagesRead", matchId, readResult.MessageIds);

            // Keep existing behavior: also notify sender for backward compat single-message events
            foreach (var messageId in readResult.MessageIds)
            {
                await Clients.Users(new[] { userId, senderId })
                    .SendAsync("MessageRead", messageId, readAt);
            }

            // Mark message notifications for this match as read
            await MarkMatchNotificationsAsReadAsync(userId, matchId);
        }

        private async Task MarkMatchNotificationsAsReadAsync(string userId, string matchId)
        {
            var candidates = await _context.Notifications
                .Where(n => n.UserId == userId && n.Type == "message" && !n.IsRead)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var hasChanges = false;

            foreach (var notification in candidates)
            {
                if (!NotificationDataParser.MatchesMatchId(notification.Data, matchId))
                {
                    continue;
                }

                notification.IsRead = true;
                notification.UpdatedAt = now;
                hasChanges = true;
            }

            if (hasChanges)
            {
                await _context.SaveChangesAsync();
            }
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier!;

            try
            {
                var matchIds = await _chatService.GetMatchIdsWithUndeliveredMessagesAsync(userId);
                foreach (var matchId in matchIds)
                {
                    var deliveredIds = await _chatService.MarkMessagesDeliveredAsync(matchId, userId);
                    if (deliveredIds.Count > 0)
                    {
                        try
                        {
                            var senderId = await _matchService.GetPeerUserIdAsync(matchId, userId);
                            await Clients.User(senderId)
                                .SendAsync("MessagesDelivered", matchId, deliveredIds);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to notify delivery for match {MatchId}", matchId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process pending deliveries on connect for user {UserId}", userId);
            }

            await base.OnConnectedAsync();
        }

        public async Task JoinChat(string matchId)
        {
            var userId = Context.UserIdentifier!;
            var chatRoomId = $"chat_{matchId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, chatRoomId);
            _logger.LogInformation("User {UserId} joined chat room {ChatRoomId}", userId, chatRoomId);

            // Batch-deliver any messages the receiver hasn't acknowledged yet
            var deliveredIds = await _chatService.MarkMessagesDeliveredAsync(matchId, userId);
            if (deliveredIds.Count > 0)
            {
                var senderId = await ResolvePeerUserIdAsync(matchId, userId);
                await Clients.User(senderId)
                    .SendAsync("MessagesDelivered", matchId, deliveredIds);
            }
        }

        public async Task LeaveChat(string matchId)
        {
            var chatRoomId = $"chat_{matchId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatRoomId);
            _logger.LogInformation($"User {Context.UserIdentifier} left chat room {chatRoomId}");
        }
    }
}
