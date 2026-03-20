using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SNIF.API.Extensions;
using SNIF.Application.Services;
using SNIF.Core.Interfaces;

namespace SNIF.Tests.Services;

public class AccountEmailServiceConfigurationTests
{
    [Fact]
    public void AddApplicationServices_DefaultProvider_UsesLoggingFallback()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());
        var services = CreateServices(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var emailService = serviceProvider.GetRequiredService<IAccountEmailService>();

        emailService.Should().BeOfType<LoggingAccountEmailService>();
    }

    [Fact]
    public void AddApplicationServices_AzureProviderWithoutSenderAddress_UsesLoggingFallback()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "AzureCommunication",
            ["Email:ConnectionString"] = "endpoint=https://example.communication.azure.com/;accesskey=ZmFrZQ=="
        });
        var services = CreateServices(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var emailService = serviceProvider.GetRequiredService<IAccountEmailService>();

        emailService.Should().BeOfType<LoggingAccountEmailService>();
    }

    [Fact]
    public void AddApplicationServices_AzureProviderWithRequiredSettings_UsesAzureCommunicationService()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "AzureCommunication",
            ["Email:ConnectionString"] = "endpoint=https://example.communication.azure.com/;accesskey=ZmFrZQ==",
            ["Email:SenderAddress"] = "noreply@snif.app"
        });
        var services = CreateServices(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var emailService = serviceProvider.GetRequiredService<IAccountEmailService>();

        emailService.Should().BeOfType<AzureCommunicationAccountEmailService>();
    }

    [Fact]
    public void AddApplicationServices_AzureCommunicationServicesProviderWithRequiredSettings_UsesAzureCommunicationService()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "AzureCommunicationServices",
            ["Email:ConnectionString"] = "endpoint=https://example.communication.azure.com/;accesskey=ZmFrZQ==",
            ["Email:SenderAddress"] = "noreply@snif.app"
        });
        var services = CreateServices(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        var emailService = serviceProvider.GetRequiredService<IAccountEmailService>();

        emailService.Should().BeOfType<AzureCommunicationAccountEmailService>();
    }

    [Fact]
    public void AddApplicationServices_ProductionAzureProviderWithoutRequiredSettings_ThrowsOnResolution()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "AzureCommunication",
            ["Email:ConnectionString"] = "",
            ["Email:SenderAddress"] = ""
        });

        var services = CreateServices(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = Environments.Production });

        using var serviceProvider = services.BuildServiceProvider();
        var act = () => serviceProvider.GetRequiredService<IAccountEmailService>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Azure Communication in Production*");
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static ServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddApplicationServices(configuration);
        return services;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "SNIF.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}