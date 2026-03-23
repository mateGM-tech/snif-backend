using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using SNIF.Core.DTOs;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace SNIF.Tests;

public class AuthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnauthenticatedRequest_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonAdmin_AccessingAdminEndpoint_Returns403()
    {
        var client = _factory.CreateClient();

        // Generate a valid JWT for a regular user (no admin role)
        var token = GenerateTestJwt("regular-user", "User");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/admin/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ValidJwt_GrantsAccessToProtectedEndpoint()
    {
        var client = _factory.CreateClient();

        // Generate a valid JWT for an admin user
        var token = GenerateTestJwt("admin-user", "Admin");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // Admin dashboard should return 200 (even if empty data)
        var response = await client.GetAsync("/api/admin/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SeededAdminUser_CanLoginAndAccessAdminDashboard()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/users/token", new LoginDto
        {
            Email = "admin@snif.hu",
            Password = CustomWebApplicationFactory.SeededPrivilegedUserPassword
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var authResponse = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        authResponse.Should().NotBeNull();
        authResponse!.Token.Should().NotBeNullOrWhiteSpace();
        authResponse.Role.Should().Be("SuperAdmin");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authResponse.Token);

        var adminResponse = await client.GetAsync("/api/admin/dashboard");

        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SeededSupportUser_CanAccessStaffReadEndpoints_ButNotAdminEndpoints()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/users/token", new LoginDto
        {
            Email = "support@snif.hu",
            Password = CustomWebApplicationFactory.SeededPrivilegedUserPassword
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var authResponse = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        authResponse.Should().NotBeNull();
        authResponse!.Token.Should().NotBeNullOrWhiteSpace();
        authResponse.Role.Should().Be("Support");

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", authResponse.Token);

        var staffReadResponse = await client.GetAsync("/api/admin/users");
        var adminResponse = await client.GetAsync("/api/admin/dashboard");

        staffReadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var client = _factory.CreateClient();

        var token = GenerateTestJwt("user1", "Admin", expired: true);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/admin/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string GenerateTestJwt(string userId, string role, bool expired = false)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            CustomWebApplicationFactory.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, role)
        };

        var expiry = expired
            ? DateTime.UtcNow.AddHours(-1)
            : DateTime.UtcNow.AddHours(1);

        var token = new JwtSecurityToken(
            issuer: "http://localhost:3000",
            audience: "http://localhost:3000",
            claims: claims,
            expires: expiry,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task UnauthenticatedBanUser_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/admin/users/some-user-id/ban", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
