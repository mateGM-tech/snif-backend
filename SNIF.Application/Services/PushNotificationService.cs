using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SNIF.Core.Entities;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;

namespace SNIF.Busniess.Services
{
    public class PushNotificationService : IPushNotificationService
    {
        private readonly SNIFContext _context;
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(SNIFContext context, ILogger<PushNotificationService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task RegisterDeviceAsync(string userId, string token, string platform)
        {
            var existing = await _context.DeviceTokens
                .FirstOrDefaultAsync(d => d.Token == token);

            if (existing != null)
            {
                existing.UserId = userId;
                existing.Platform = platform;
                existing.LastUsedAt = DateTime.UtcNow;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _context.DeviceTokens.Add(new DeviceToken
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = userId,
                    Token = token,
                    Platform = platform,
                    LastUsedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
        }

        public async Task UnregisterDeviceAsync(string userId, string token)
        {
            var deviceToken = await _context.DeviceTokens
                .FirstOrDefaultAsync(d => d.Token == token && d.UserId == userId);

            if (deviceToken != null)
            {
                _context.DeviceTokens.Remove(deviceToken);
                await _context.SaveChangesAsync();
            }
        }

        public async Task SendPushAsync(string userId, string title, string body, Dictionary<string, string>? data = null)
        {
            var now = DateTime.UtcNow;
            var notification = new Notification
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Type = data?.GetValueOrDefault("type") ?? "system",
                Title = title,
                Body = body,
                Data = data != null ? System.Text.Json.JsonSerializer.Serialize(data) : null,
                IsRead = false,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.Notifications.Add(notification);

            var tokens = await _context.DeviceTokens
                .Where(d => d.UserId == userId)
                .ToListAsync();

            if (tokens.Count == 0)
            {
                _logger.LogDebug("No device tokens found for user {UserId}, skipping push notification", userId);
                await _context.SaveChangesAsync();
                return;
            }

            var messaging = FirebaseAdmin.Messaging.FirebaseMessaging.DefaultInstance;
            if (messaging == null)
            {
                _logger.LogWarning("FirebaseMessaging not initialized. Skipping FCM for user {UserId}", userId);
            }
            else
            {
                foreach (var token in tokens.ToList())
                {
                    try
                    {
                        var message = new FirebaseAdmin.Messaging.Message
                        {
                            Token = token.Token,
                            Notification = new FirebaseAdmin.Messaging.Notification
                            {
                                Title = title,
                                Body = body
                            },
                            Data = data
                        };

                        await messaging.SendAsync(message);
                        token.LastUsedAt = DateTime.UtcNow;
                    }
                    catch (FirebaseAdmin.Messaging.FirebaseMessagingException ex) when (ex.MessagingErrorCode == FirebaseAdmin.Messaging.MessagingErrorCode.Unregistered)
                    {
                        _context.DeviceTokens.Remove(token);
                        _logger.LogWarning("Removed unregistered device token for user {UserId}", userId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send push notification to {Platform} device for user {UserId}", token.Platform, userId);
                    }
                }
            }

            await _context.SaveChangesAsync();
        }
    }
}
