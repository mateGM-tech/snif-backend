using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SNIF.Application.Services;
using SNIF.Core.Configuration;

namespace SNIF.API.HealthChecks;

public class EmailDeliveryHealthCheck : IHealthCheck
{
    private readonly IOptions<EmailOptions> _emailOptions;
    private readonly EmailDeliveryHealthState _deliveryHealthState;
    private readonly IHostEnvironment _hostEnvironment;

    public EmailDeliveryHealthCheck(
        IOptions<EmailOptions> emailOptions,
        EmailDeliveryHealthState deliveryHealthState,
        IHostEnvironment hostEnvironment)
    {
        _emailOptions = emailOptions;
        _deliveryHealthState = deliveryHealthState;
        _hostEnvironment = hostEnvironment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var options = _emailOptions.Value;

        if (UsesAzureEmailProvider(options.Provider))
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString) || string.IsNullOrWhiteSpace(options.SenderAddress))
            {
                var result = _hostEnvironment.IsProduction()
                    ? HealthCheckResult.Unhealthy("Azure email provider selected but required email configuration is missing.")
                    : HealthCheckResult.Degraded("Azure email provider selected but required email configuration is missing.");

                return Task.FromResult(result);
            }

            var failures = _deliveryHealthState.ConsecutiveFailures;
            if (failures >= 3)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Azure email delivery has {failures} consecutive failures. Last failure: {_deliveryHealthState.LastFailureReason}"));
            }

            if (failures > 0)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Azure email delivery has {failures} consecutive failures. Last failure: {_deliveryHealthState.LastFailureReason}"));
            }

            return Task.FromResult(HealthCheckResult.Healthy("Azure email delivery is configured and no recent send failures were recorded."));
        }

        if (_hostEnvironment.IsProduction())
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Logging email provider is active in Production. Configure AzureCommunication email provider for live delivery."));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Logging email provider active for non-production environment."));
    }

    private static bool UsesAzureEmailProvider(string? provider)
    {
        return provider != null &&
               (provider.Equals("AzureCommunication", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("AzureCommunicationServices", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("Azure", StringComparison.OrdinalIgnoreCase));
    }
}
