using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Core.Constants;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Exceptions;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests.Services;

public class UserServiceAuthFlowTests
{
    private static SNIFContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase("UserServiceAuthFlowTests_" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SNIFContext(options);
    }

    private static Mock<UserManager<User>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<User>>();
        return new Mock<UserManager<User>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private static Mock<SignInManager<User>> CreateMockSignInManager(UserManager<User> userManager)
    {
        return new Mock<SignInManager<User>>(
            userManager,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(),
            null!,
            null!,
            null!,
            null!);
    }

    private static UserService CreateService(
        SNIFContext context,
        Mock<UserManager<User>> userManager,
        Mock<IAccountEmailService> accountEmailService)
    {
        return new UserService(
            Mock.Of<ITokenService>(),
            CreateMockSignInManager(userManager.Object).Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            Mock.Of<AutoMapper.IMapper>(),
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);
    }

    [Fact]
    public async Task ForgotPasswordAsync_KnownUser_GeneratesTokenAndSendsEmail()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var user = new User
        {
            Id = "user-1",
            Email = "known@example.com",
            UserName = "known",
            Name = "Known User"
        };

        userManager.Setup(m => m.FindByEmailAsync("known@example.com")).ReturnsAsync(user);
        userManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        accountEmailService.Setup(m => m.SendPasswordResetAsync(user, It.IsAny<string>())).Returns(Task.CompletedTask);

        var service = CreateService(context, userManager, accountEmailService);

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "known@example.com" });

        user.PasswordResetToken.Should().NotBeNullOrWhiteSpace();
        user.PasswordResetTokenExpiry.Should().BeAfter(DateTime.UtcNow.AddMinutes(50));
        accountEmailService.Verify(m => m.SendPasswordResetAsync(user, user.PasswordResetToken!), Times.Once);
    }

    [Fact]
    public async Task ResendConfirmationAsync_UnconfirmedUser_RegeneratesTokenAndSendsEmail()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var user = new User
        {
            Id = "user-2",
            Email = "pending@example.com",
            UserName = "pending",
            Name = "Pending User",
            EmailConfirmed = false,
            EmailConfirmationToken = "old-token"
        };

        userManager.Setup(m => m.FindByEmailAsync("pending@example.com")).ReturnsAsync(user);
        userManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        accountEmailService.Setup(m => m.SendEmailConfirmationAsync(user, It.IsAny<string>())).Returns(Task.CompletedTask);

        var service = CreateService(context, userManager, accountEmailService);

        await service.ResendConfirmationAsync(new ResendConfirmationDto { Email = "pending@example.com" });

        user.EmailConfirmationToken.Should().NotBe("old-token");
        accountEmailService.Verify(m => m.SendEmailConfirmationAsync(user, user.EmailConfirmationToken!), Times.Once);
    }

    [Fact]
    public async Task RegisterUserAsync_LocalAccount_ReturnsPendingActivationWithoutToken()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var mapper = new Mock<AutoMapper.IMapper>();
        var tokenService = new Mock<ITokenService>();

        userManager.Setup(m => m.FindByEmailAsync("pending@example.com")).ReturnsAsync((User?)null);
        userManager.Setup(m => m.CreateAsync(It.IsAny<User>(), "ValidPass123!"))
            .ReturnsAsync(IdentityResult.Success);
        accountEmailService.Setup(m => m.SendEmailConfirmationAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mapper.Setup(m => m.Map<AuthResponseDto>(It.IsAny<User>()))
            .Returns((User user) => new AuthResponseDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                Name = user.Name ?? string.Empty,
                CreatedAt = user.CreatedAt,
                EmailConfirmed = user.EmailConfirmed
            });

        var service = new UserService(
            tokenService.Object,
            CreateMockSignInManager(userManager.Object).Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            mapper.Object,
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);

        var response = await service.RegisterUserAsync(new CreateUserDto
        {
            Email = "pending@example.com",
            Password = "ValidPass123!",
            Name = "PendingUser"
        });

        response.Token.Should().BeNull();
        response.AuthStatus.Should().Be("PendingActivation");
        response.RequiresEmailConfirmation.Should().BeTrue();
        response.CanResendConfirmation.Should().BeTrue();
        response.EmailConfirmed.Should().BeFalse();
        accountEmailService.Verify(m => m.SendEmailConfirmationAsync(It.IsAny<User>(), It.IsAny<string>()), Times.Once);
        tokenService.Verify(m => m.CreateTokenAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task RegisterUserAsync_DisplayNameWithSpaces_PreservesNameAndUsesInternalUsername()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var mapper = new Mock<AutoMapper.IMapper>();
        var createdUser = default(User);

        userManager.Setup(m => m.FindByEmailAsync("spacey@example.com")).ReturnsAsync((User?)null);
        userManager.Setup(m => m.CreateAsync(It.IsAny<User>(), "ValidPass123!"))
            .Callback<User, string>((user, _) => createdUser = user)
            .ReturnsAsync(IdentityResult.Success);
        accountEmailService.Setup(m => m.SendEmailConfirmationAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mapper.Setup(m => m.Map<AuthResponseDto>(It.IsAny<User>()))
            .Returns((User user) => new AuthResponseDto
            {
                Id = user.Id,
                Email = user.Email ?? string.Empty,
                Name = user.Name ?? string.Empty,
                CreatedAt = user.CreatedAt,
                EmailConfirmed = user.EmailConfirmed
            });

        var service = new UserService(
            Mock.Of<ITokenService>(),
            CreateMockSignInManager(userManager.Object).Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            mapper.Object,
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);

        await service.RegisterUserAsync(new CreateUserDto
        {
            Email = "spacey@example.com",
            Password = "ValidPass123!",
            Name = "Mary Jane Watson"
        });

        createdUser.Should().NotBeNull();
        createdUser!.Name.Should().Be("Mary Jane Watson");
        createdUser.UserName.Should().MatchRegex("^usr_[a-f0-9]{32}$");
        createdUser.UserName.Should().NotBe(createdUser.Name);
        createdUser.UserName.Should().NotContain(" ");
    }

    [Fact]
    public async Task ForgotPasswordAsync_UnknownUser_DoesNotSendEmail()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();

        userManager.Setup(m => m.FindByEmailAsync("missing@example.com")).ReturnsAsync((User?)null);

        var service = CreateService(context, userManager, accountEmailService);

        await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "missing@example.com" });

        accountEmailService.Verify(m => m.SendPasswordResetAsync(It.IsAny<User>(), It.IsAny<string>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────
    // Login Flow Tests
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task LoginUserAsync_ValidCredentials_ReturnsAuthenticatedWithToken()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var tokenService = new Mock<ITokenService>();
        var mapper = new Mock<AutoMapper.IMapper>();
        var signInManager = CreateMockSignInManager(userManager.Object);

        var user = new User
        {
            Id = "login-user-1",
            Email = "login@example.com",
            UserName = "login-user",
            Name = "Login User",
            EmailConfirmed = true,
            PasswordHash = "hashed"
        };

        // Seed user directly in InMemory DB so _userManager.Users queries work
        context.Users.Add(user);
        await context.SaveChangesAsync();

        userManager.Setup(m => m.Users).Returns(context.Users);
        signInManager.Setup(m => m.CheckPasswordSignInAsync(
            It.Is<User>(u => u.Id == user.Id), "CorrectPassword!", false))
            .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
        userManager.Setup(m => m.GetRolesAsync(It.Is<User>(u => u.Id == user.Id)))
            .ReturnsAsync(new List<string> { AppRoles.Admin });
        tokenService.Setup(m => m.CreateTokenAsync(It.Is<User>(u => u.Id == user.Id))).ReturnsAsync("jwt-token-123");
        mapper.Setup(m => m.Map<AuthResponseDto>(It.Is<User>(u => u.Id == user.Id)))
            .Returns(new AuthResponseDto
            {
                Id = user.Id,
                Email = user.Email!,
                Name = user.Name!,
                EmailConfirmed = true
            });

        var service = new UserService(
            tokenService.Object,
            signInManager.Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            mapper.Object,
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);

        var response = await service.LoginUserAsync(new LoginDto
        {
            Email = "login@example.com",
            Password = "CorrectPassword!"
        });

        response.Token.Should().Be("jwt-token-123");
        response.Role.Should().Be(AppRoles.Admin);
        response.AuthStatus.Should().Be("Authenticated");
        response.RequiresEmailConfirmation.Should().BeFalse();
        tokenService.Verify(m => m.CreateTokenAsync(It.Is<User>(u => u.Id == user.Id)), Times.Once);
    }

    [Fact]
    public async Task LoginUserAsync_WrongPassword_ThrowsUnauthorized()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var signInManager = CreateMockSignInManager(userManager.Object);

        var user = new User
        {
            Id = "login-user-2",
            Email = "wrong@example.com",
            UserName = "wrong-user",
            Name = "Wrong User",
            EmailConfirmed = true,
            PasswordHash = "hashed"
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        userManager.Setup(m => m.Users).Returns(context.Users);
        signInManager.Setup(m => m.CheckPasswordSignInAsync(
            It.Is<User>(u => u.Id == user.Id), "WrongPassword!", false))
            .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Failed);

        var service = new UserService(
            Mock.Of<ITokenService>(),
            signInManager.Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            Mock.Of<AutoMapper.IMapper>(),
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);

        await service.Invoking(s => s.LoginUserAsync(new LoginDto
        {
            Email = "wrong@example.com",
            Password = "WrongPassword!"
        })).Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Invalid email or password*");
    }

    [Fact]
    public async Task LoginUserAsync_UnknownEmail_ThrowsUnauthorized()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();

        // Empty DB — no users
        userManager.Setup(m => m.Users).Returns(context.Users);

        var service = CreateService(context, userManager, accountEmailService);

        await service.Invoking(s => s.LoginUserAsync(new LoginDto
        {
            Email = "nonexistent@example.com",
            Password = "AnyPassword!"
        })).Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Invalid email or password*");
    }

    [Fact]
    public async Task LoginUserAsync_UnconfirmedEmail_ThrowsPendingActivation()
    {
        using var context = CreateContext();
        var userManager = CreateMockUserManager();
        var accountEmailService = new Mock<IAccountEmailService>();
        var mapper = new Mock<AutoMapper.IMapper>();
        var signInManager = CreateMockSignInManager(userManager.Object);

        var user = new User
        {
            Id = "unconfirmed-user",
            Email = "unconfirmed@example.com",
            UserName = "unconfirmed",
            Name = "Unconfirmed",
            EmailConfirmed = false,
            PasswordHash = "hashed"
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        userManager.Setup(m => m.Users).Returns(context.Users);
        signInManager.Setup(m => m.CheckPasswordSignInAsync(
            It.Is<User>(u => u.Id == user.Id), "ValidPass!", false))
            .ReturnsAsync(Microsoft.AspNetCore.Identity.SignInResult.Success);
        mapper.Setup(m => m.Map<AuthResponseDto>(It.Is<User>(u => u.Id == user.Id)))
            .Returns(new AuthResponseDto
            {
                Id = user.Id,
                Email = user.Email!,
                Name = user.Name!,
                EmailConfirmed = false
            });

        var service = new UserService(
            Mock.Of<ITokenService>(),
            signInManager.Object,
            userManager.Object,
            context,
            Mock.Of<IWebHostEnvironment>(),
            mapper.Object,
            Mock.Of<IMediaStorageService>(),
            Mock.Of<IGoogleAuthService>(),
            Mock.Of<IEntitlementService>(),
            accountEmailService.Object);

        await service.Invoking(s => s.LoginUserAsync(new LoginDto
        {
            Email = "unconfirmed@example.com",
            Password = "ValidPass!"
        })).Should().ThrowAsync<PendingActivationException>();
    }
}