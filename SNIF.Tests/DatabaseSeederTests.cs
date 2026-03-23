using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SNIF.Core.Constants;
using SNIF.Core.Entities;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests;

public class DatabaseSeederTests
{
    private const string PrivilegedSeedPassword = "SeederTest123!";

    private static ServiceProvider BuildServiceProvider(
        string databaseName,
        IDictionary<string, string?>? configurationValues = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        var configurationBuilder = new ConfigurationBuilder();
        if (configurationValues != null)
        {
            configurationBuilder.AddInMemoryCollection(configurationValues);
        }

        services.AddSingleton<IConfiguration>(configurationBuilder.Build());
        services.AddDbContext<SNIFContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddIdentityCore<User>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<SNIFContext>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SeedAsync_CreatesPrivilegedUsersWithRoles_AndDoesNotDuplicateThem()
    {
        using var serviceProvider = BuildServiceProvider(
            Guid.NewGuid().ToString(),
            new Dictionary<string, string?>
            {
                ["SeedUsers:PrivilegedUsers:Enabled"] = "true",
                ["SeedUsers:PrivilegedUsers:InitialPassword"] = PrivilegedSeedPassword
            });

        using (var firstScope = serviceProvider.CreateScope())
        {
            await DatabaseSeeder.SeedAsync(firstScope.ServiceProvider);
        }

        using (var secondScope = serviceProvider.CreateScope())
        {
            await DatabaseSeeder.SeedAsync(secondScope.ServiceProvider);
        }

        using var verificationScope = serviceProvider.CreateScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<SNIFContext>();
        var userManager = verificationScope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var adminUser = await userManager.FindByEmailAsync("admin@snif.hu");
        var supportUser = await userManager.FindByEmailAsync("support@snif.hu");

        adminUser.Should().NotBeNull();
        supportUser.Should().NotBeNull();
        adminUser!.EmailConfirmed.Should().BeTrue();
        supportUser!.EmailConfirmed.Should().BeTrue();

        (await userManager.CheckPasswordAsync(adminUser, PrivilegedSeedPassword)).Should().BeTrue();
        (await userManager.CheckPasswordAsync(supportUser, PrivilegedSeedPassword)).Should().BeTrue();

        var adminRoles = await userManager.GetRolesAsync(adminUser);
        var supportRoles = await userManager.GetRolesAsync(supportUser);

        adminRoles.Should().Contain(new[] { AppRoles.SuperAdmin, AppRoles.Admin });
        supportRoles.Should().Contain(AppRoles.Support);
        supportRoles.Should().NotContain(AppRoles.Admin);

        context.Users.Count(user => user.Email == "admin@snif.hu").Should().Be(1);
        context.Users.Count(user => user.Email == "support@snif.hu").Should().Be(1);
    }

    [Fact]
    public async Task SeedAsync_DoesNotCreatePrivilegedUsers_WhenPrivilegedSeedingIsDisabled()
    {
        using var serviceProvider = BuildServiceProvider(Guid.NewGuid().ToString());

        using (var scope = serviceProvider.CreateScope())
        {
            await DatabaseSeeder.SeedAsync(scope.ServiceProvider);
        }

        using var verificationScope = serviceProvider.CreateScope();
        var userManager = verificationScope.ServiceProvider.GetRequiredService<UserManager<User>>();

        (await userManager.FindByEmailAsync("admin@snif.hu")).Should().BeNull();
        (await userManager.FindByEmailAsync("support@snif.hu")).Should().BeNull();
    }

    [Fact]
    public async Task SeedAsync_Throws_WhenPrivilegedSeedingEnabledWithoutPassword()
    {
        using var serviceProvider = BuildServiceProvider(
            Guid.NewGuid().ToString(),
            new Dictionary<string, string?>
            {
                ["SeedUsers:PrivilegedUsers:Enabled"] = "true"
            });

        using var scope = serviceProvider.CreateScope();

        await FluentActions.Invoking(() => DatabaseSeeder.SeedAsync(scope.ServiceProvider))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SeedUsers:PrivilegedUsers:InitialPassword*");
    }
}