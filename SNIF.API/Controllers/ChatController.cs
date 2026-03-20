using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Identity.Client;
using SNIF.API.Extensions;
using SNIF.Busniess.Services;
using SNIF.Core.DTOs;
using SNIF.Core.Interfaces;
using SNIF.SignalR.Hubs;
using System.Security.Claims;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/chats")]
    [Authorize]
    [EnableRateLimiting("global")]
    public class ChatController : ControllerBase
    {
        private readonly IChatService _chatService;
        private readonly IMatchService _matchService;
        private readonly IMediaStorageService _mediaStorage;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly IPushNotificationService _pushNotificationService;

        public ChatController(IChatService chatService, IMatchService matchService, IMediaStorageService mediaStorage, IHubContext<ChatHub> hubContext, IPushNotificationService pushNotificationService)
        {
            _chatService = chatService;
            _matchService = matchService;
            _mediaStorage = mediaStorage;
            _hubContext = hubContext;
            _pushNotificationService = pushNotificationService;
        }

        // GET api/chats/matches/{matchId}/messages
        [HttpGet("matches/{matchId}/messages")]
        [ProducesResponseType(typeof(IEnumerable<MessageDto>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<IEnumerable<MessageDto>>> GetMatchMessages(string matchId)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                var match = await _matchService.GetMatchByIdAsync(matchId);
                if (match.InitiatorPet.OwnerId != authUserId && match.TargetPet.OwnerId != authUserId)
                    return Unauthorized(new ErrorResponse { Message = "User not authorized to view these messages" });

                var messages = await _chatService.GetMatchMessagesAsync(matchId);
                return Ok(messages);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Match not found" });
            }
        }

        // GET api/chats/users/{userId}
        [HttpGet("users/{userId}")]
        [ProducesResponseType(typeof(IEnumerable<ChatSummaryDto>), StatusCodes.Status200OK)]
        public async Task<ActionResult<IEnumerable<ChatSummaryDto>>> GetUserChats(string userId)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId) || authUserId != userId)
                return Unauthorized(new ErrorResponse { Message = "Unauthorized access" });

            try
            {
                var chats = await _chatService.GetUserChatsAsync(userId);
                return Ok(chats);
            }
            catch (Exception)
            {
                throw;
            }
        }


        [HttpPatch("messages/{messageId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult> MarkAsRead(string messageId)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            if (string.IsNullOrEmpty(messageId))
                return BadRequest(new ErrorResponse { Message = "Message ID is required" });

            try
            {
                await _chatService.MarkAsReadAsync(messageId, authUserId);
                return Ok();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Message not found" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new ErrorResponse { Message = "An error occurred while marking message as read" });
            }
        }

        // POST api/chats/messages/{messageId}/reactions
        [HttpPost("messages/{messageId}/reactions")]
        [ProducesResponseType(typeof(MessageReactionDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<MessageReactionDto>> AddReaction(string messageId, [FromBody] AddReactionDto dto)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            var allowedEmoji = new HashSet<string> { "❤️", "😂", "👍", "😮", "😢", "🙏" };
            if (!allowedEmoji.Contains(dto.Emoji))
                return BadRequest(new ErrorResponse { Message = "Invalid emoji" });

            try
            {
                var reaction = await _chatService.AddReactionAsync(messageId, authUserId, dto.Emoji);
                return Ok(reaction);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Message not found" });
            }
        }

        // DELETE api/chats/messages/{messageId}/reactions/{emoji}
        [HttpDelete("messages/{messageId}/reactions/{emoji}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult> RemoveReaction(string messageId, string emoji)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                var removed = await _chatService.RemoveReactionAsync(messageId, authUserId, emoji);
                if (!removed)
                    return NotFound(new ErrorResponse { Message = "Reaction not found" });

                return NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Message not found" });
            }
        }

        // POST api/chats/matches/{matchId}/messages/image
        [HttpPost("matches/{matchId}/messages/image")]
        [RequestSizeLimit(10_000_000)]
        [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<MessageDto>> SendImageMessage(string matchId, IFormFile image)
        {
            var authUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(authUserId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            if (image == null || image.Length == 0)
                return BadRequest(new ErrorResponse { Message = "No image provided" });

            var allowedTypes = new HashSet<string> { "image/jpeg", "image/png", "image/webp", "image/gif" };
            if (!allowedTypes.Contains(image.ContentType))
                return BadRequest(new ErrorResponse { Message = "Invalid image type. Allowed: jpg, png, webp, gif" });

            try
            {
                var receiverId = await _matchService.GetPeerUserIdAsync(matchId, authUserId);

                var fileName = $"chat/{matchId}/{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
                using var stream = image.OpenReadStream();
                var url = await _mediaStorage.UploadAsync(stream, fileName, image.ContentType);

                var message = await _chatService.SendImageMessageAsync(
                    matchId, authUserId, receiverId, url, image.FileName, image.Length);

                // Broadcast via SignalR so the receiver sees the image in real-time
                await _hubContext.Clients.Users(new[] { authUserId, receiverId })
                    .SendAsync("ReceiveMessage", message);

                // Send push notification for offline delivery
                await _pushNotificationService.SendPushAsync(
                    receiverId,
                    "New Message \ud83d\udcac",
                    "\ud83d\udcf7 Photo",
                    new Dictionary<string, string> { ["type"] = "message", ["matchId"] = matchId });

                return Ok(message);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new ErrorResponse { Message = "User not authorized" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new ErrorResponse { Message = "Match not found" });
            }
        }
    }
}
