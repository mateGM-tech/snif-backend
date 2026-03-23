using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestJwtKey = "test-secret-key-that-is-long-enough-for-hmac-sha512-at-least-64-characters-long-test-only";
    public const string SeededPrivilegedUserPassword = "TestPrivileged123!";
    private readonly string _databaseName = "SNIFTestDb_" + Guid.NewGuid();

    public static string JwtKey => TestJwtKey;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("Jwt__Key", TestJwtKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "http://localhost:3000");
        Environment.SetEnvironmentVariable("Jwt__Audience", "http://localhost:3000");
        Environment.SetEnvironmentVariable("Google__ClientId", "test-google-client-id");

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = "http://localhost:3000",
                ["Jwt:Audience"] = "http://localhost:3000",
                ["ConnectionStrings:DefaultConnection"] = "",
                ["Google:ClientId"] = "test-google-client-id",
                ["LemonSqueezy:SigningSecret"] = "test-webhook-secret-key",
                ["LemonSqueezy:Variants:TreatBag10"] = "variant-10",
                ["LemonSqueezy:Variants:TreatBag50"] = "variant-50",
                ["LemonSqueezy:Variants:TreatBag100"] = "variant-100",
                ["SeedUsers:PrivilegedUsers:Enabled"] = "true",
                ["SeedUsers:PrivilegedUsers:InitialPassword"] = SeededPrivilegedUserPassword
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove ALL DbContext-related registrations (including Npgsql)
            var dbContextDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<SNIFContext>)
                    || d.ServiceType == typeof(DbContextOptions)
                    || (d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
                       && d.ServiceType.FullName?.Contains("Options") == true))
                .ToList();
            foreach (var d in dbContextDescriptors)
                services.Remove(d);

            // Also remove the SNIFContext registration itself
            var contextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(SNIFContext));
            if (contextDescriptor != null)
                services.Remove(contextDescriptor);

            // Remove health check registrations that depend on real services
            var healthCheckDescriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("HealthCheck") == true)
                .ToList();
            foreach (var hc in healthCheckDescriptors)
                services.Remove(hc);

            // Add InMemory database
            services.AddDbContext<SNIFContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });

            // Re-add basic health checks without external dependencies
            services.AddHealthChecks()
                .AddCheck("database", () =>
                    HealthCheckResult.Healthy("In-memory test database"));
        });
    }
}
