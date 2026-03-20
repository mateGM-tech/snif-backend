using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SNIF.API.HealthChecks;
using SNIF.Application.Services;
using SNIF.Core.Configuration;

namespace SNIF.Tests.HealthChecks;

public class EmailDeliveryHealthCheckTests
{
    [Fact]
    public async Task AzureProviderMissingSecrets_InProduction_ReturnsUnhealthy()
    {
        var check = CreateHealthCheck(
            options: new EmailOptions
            {
                Provider = "AzureCommunication",
                ConnectionString = "",
                SenderAddress = ""
            },
            environmentName: Environments.Production);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task AzureProviderMissingSecrets_InDevelopment_ReturnsDegraded()
    {
        var check = CreateHealthCheck(
            options: new EmailOptions
            {
                Provider = "AzureCommunication",
                ConnectionString = "",
                SenderAddress = ""
            },
            environmentName: Environments.Development);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task AzureProviderConfigured_TransitionsHealthyToDegradedToUnhealthy_AndBackOnSuccess()
    {
        var state = new EmailDeliveryHealthState();
        var check = CreateHealthCheck(
            options: new EmailOptions
            {
                Provider = "AzureCommunication",
                ConnectionString = "endpoint=https://example.communication.azure.com/;accesskey=fake",
                SenderAddress = "no-reply@example.com"
            },
            environmentName: Environments.Production,
            state: state);

        var healthy = await check.CheckHealthAsync(new HealthCheckContext());
        healthy.Status.Should().Be(HealthStatus.Healthy);

        state.RecordFailure("transient");
        var degraded = await check.CheckHealthAsync(new HealthCheckContext());
        degraded.Status.Should().Be(HealthStatus.Degraded);

        state.RecordFailure("transient");
        state.RecordFailure("persistent");
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext());
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);

        state.RecordSuccess();
        var healthyAgain = await check.CheckHealthAsync(new HealthCheckContext());
        healthyAgain.Status.Should().Be(HealthStatus.Healthy);
    }

    private static EmailDeliveryHealthCheck CreateHealthCheck(
        EmailOptions options,
        string environmentName,
        EmailDeliveryHealthState? state = null)
    {
        return new EmailDeliveryHealthCheck(
            Options.Create(options),
            state ?? new EmailDeliveryHealthState(),
            new TestHostEnvironment { EnvironmentName = environmentName });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SNIF.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
