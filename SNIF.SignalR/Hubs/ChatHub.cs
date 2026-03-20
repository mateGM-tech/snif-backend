using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
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
            var message = await _chatService.GetMessageByIdAsync(messageId);
            if (message == null) return;

            // Only the receiver can confirm delivery
            if (message.ReceiverId != userId) return;

            await Clients.User(message.SenderId)
                .SendAsync("MessageDelivered", messageId);
        }

        public async Task MarkMessageAsRead(string messageId)
        {
            var userId = Context.UserIdentifier!;
            var message = await _chatService.MarkAsReadAsync(messageId);

            if (message != null)
            {
                await Clients.Users(new[] { userId, message.SenderId })
                    .SendAsync("MessageRead", messageId);
            }
        }

        public async Task MarkConversationAsRead(string matchId)
        {
            var userId = Context.UserIdentifier!;

            // Find all unread messages in this match where the current user is the receiver
            var unreadMessages = await _context.Messages
                .Where(m => m.MatchId == matchId && m.ReceiverId == userId && !m.IsRead)
                .ToListAsync();

            if (unreadMessages.Count == 0) return;

            foreach (var msg in unreadMessages)
            {
                msg.IsRead = true;
            }
            await _context.SaveChangesAsync();

            // Notify senders that their messages were read
            var senderIds = unreadMessages.Select(m => m.SenderId).Distinct().ToList();
            foreach (var messageId in unreadMessages.Select(m => m.Id))
            {
                await Clients.Users(senderIds.Concat(new[] { userId }).ToArray())
                    .SendAsync("MessageRead", messageId);
            }

            // Mark message notifications for this match as read
            await _context.Notifications
                .Where(n => n.UserId == userId && n.Type == "message" && !n.IsRead
                    && n.Data != null && n.Data.Contains(matchId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.IsRead, true)
                    .SetProperty(n => n.UpdatedAt, DateTime.UtcNow));
        }

        public async Task JoinChat(string matchId)
        {
            var chatRoomId = $"chat_{matchId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, chatRoomId);
            _logger.LogInformation($"User {Context.UserIdentifier} joined chat room {chatRoomId}");
        }

        public async Task LeaveChat(string matchId)
        {
            var chatRoomId = $"chat_{matchId}";
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatRoomId);
            _logger.LogInformation($"User {Context.UserIdentifier} left chat room {chatRoomId}");
        }
    }
}
