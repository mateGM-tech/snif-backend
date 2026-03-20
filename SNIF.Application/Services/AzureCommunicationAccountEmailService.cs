using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SNIF.Core.Configuration;
using SNIF.Core.Entities;
using SNIF.Core.Interfaces;

namespace SNIF.Application.Services
{
    public class AzureCommunicationAccountEmailService : IAccountEmailService
    {
        private readonly ILogger<AzureCommunicationAccountEmailService> _logger;
        private readonly IConfiguration _configuration;
        private readonly EmailOptions _options;
        private readonly EmailClient _emailClient;
        private readonly EmailDeliveryHealthState _deliveryHealthState;

        public AzureCommunicationAccountEmailService(
            ILogger<AzureCommunicationAccountEmailService> logger,
            IConfiguration configuration,
            IOptions<EmailOptions> options,
            EmailDeliveryHealthState deliveryHealthState)
        {
            _logger = logger;
            _configuration = configuration;
            _options = options.Value;
            _deliveryHealthState = deliveryHealthState;

            if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            {
                throw new InvalidOperationException("Email:ConnectionString must be configured for Azure email delivery.");
            }

            _emailClient = new EmailClient(_options.ConnectionString);
        }

        public Task SendPasswordResetAsync(User user, string token)
        {
            var content = AccountEmailContentFactory.CreatePasswordReset(user, token, _configuration["App:PublicBaseUrl"]);
            return SendAsync(user, content);
        }

        public Task SendEmailConfirmationAsync(User user, string token)
        {
            var content = AccountEmailContentFactory.CreateEmailConfirmation(user, token, _configuration["App:PublicBaseUrl"]);
            return SendAsync(user, content);
        }

        private async Task SendAsync(User user, AccountEmailContent content)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                _logger.LogWarning("Skipping {Action} email because the recipient address is missing for user {UserId}.", content.Action, user.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.SenderAddress))
            {
                _logger.LogError("Azure Communication email is enabled, but Email:SenderAddress is missing. {Action} email for {Email} was not sent.", content.Action, user.Email);
                _deliveryHealthState.RecordFailure("Email:SenderAddress is missing.");
                return;
            }

            if (string.IsNullOrWhiteSpace(content.Link))
            {
                _logger.LogWarning("App:PublicBaseUrl is not configured. Sending {Action} email to {Email} without an action link.", content.Action, user.Email);
            }

            try
            {
                await _emailClient.SendAsync(
                    WaitUntil.Completed,
                    _options.SenderAddress,
                    user.Email,
                    content.Subject,
                    content.PlainTextBody,
                    content.HtmlBody);

                _deliveryHealthState.RecordSuccess();
                _logger.LogInformation("Sent {Action} email to {Email} using Azure Communication Services.", content.Action, user.Email);
            }
            catch (Exception ex)
            {
                _deliveryHealthState.RecordFailure(ex.Message);
                _logger.LogError(ex, "Failed to send {Action} email to {Email} using Azure Communication Services.", content.Action, user.Email);
            }
        }
    }
}