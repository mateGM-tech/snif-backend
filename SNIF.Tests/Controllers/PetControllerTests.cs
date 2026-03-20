using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SNIF.Core.Entities;
using SNIF.Infrastructure.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;

namespace SNIF.Tests.Controllers;

public class PetControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PetControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string GenerateTestJwt(string userId, string role = "User")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(CustomWebApplicationFactory.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId),
            new Claim(ClaimTypes.Role, role)
        };
        var token = new JwtSecurityToken(
            issuer: "http://localhost:3000",
            audience: "http://localhost:3000",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task GetPets_WithAuth_Returns200()
    {
        var client = _factory.CreateClient();
        var token = GenerateTestJwt("user1");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/pets?userId=user1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPets_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        // GET /api/pets without auth - endpoint now requires auth
        var response = await client.GetAsync("/api/pets?userId=test");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreatePet_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/pets", new
        {
            Name = "Buddy",
            Species = "Dog",
            Breed = "Labrador",
            Age = 3
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPetById_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/pets/nonexistent-id");

        // Endpoint now requires auth at class level
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeletePet_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync("/api/pets/some-id");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdatePet_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/pets/some-id", new { Name = "Updated" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPets_ForOtherUser_Returns403()
    {
        var client = _factory.CreateClient();
        var token = GenerateTestJwt("user1");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // user1 tries to list user2's pets
        var response = await client.GetAsync("/api/pets?userId=user2");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPetById_AsNonOwner_ReturnsPublicPetDto()
    {
        // Seed a pet directly in the DB
        var petId = "test-pet-" + Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SNIFContext>();
            db.Users.Add(new User { Id = "owner1", UserName = "owner1", Email = "o1@test.com", Name = "Owner One" });
            db.Pets.Add(new Pet
            {
                Id = petId,
                Name = "Buddy",
                Species = "Dog",
                Breed = "Labrador",
                Age = 3,
                OwnerId = "owner1",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var otherToken = GenerateTestJwt("other-user");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);

        var getResponse = await client.GetAsync($"/api/pets/{petId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync();

        // PublicPetDto must NOT contain sensitive owner fields
        body.Should().NotContain("\"ownerId\"", because: "non-owners should not see OwnerId");
        body.Should().NotContain("\"medicalHistory\"", because: "non-owners should not see medical data");
        body.Should().NotContain("\"isLocked\"", because: "non-owners should not see lock status");

        // PublicPetDto must contain public fields
        body.Should().Contain("Buddy");
        body.Should().Contain("Labrador");
    }

    [Fact]
    public async Task GetPetById_AsOwner_ReturnsFullPetDto()
    {
        // Seed a pet directly in the DB
        var petId = "test-pet-owner-" + Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SNIFContext>();
            var existingUser = await db.Users.FindAsync("owner2");
            if (existingUser == null)
                db.Users.Add(new User { Id = "owner2", UserName = "owner2", Email = "o2@test.com", Name = "Owner Two" });
            db.Pets.Add(new Pet
            {
                Id = petId,
                Name = "Luna",
                Species = "Cat",
                Breed = "Siamese",
                Age = 2,
                OwnerId = "owner2",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var ownerToken = GenerateTestJwt("owner2");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        var getResponse = await client.GetAsync($"/api/pets/{petId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResponse.Content.ReadAsStringAsync();

        // Full PetDto includes OwnerId
        body.Should().Contain("owner2", because: "owners should see their own OwnerId");
        body.Should().Contain("Luna");
    }
}
