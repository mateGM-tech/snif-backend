using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SNIF.Infrastructure.Data;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace SNIF.Tests.Controllers;

public class PaymentControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PaymentControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string ComputeHmacSignature(string payload, string secret)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
    public async Task CreateCheckoutSession_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/payments/create-checkout-session", new
        {
            Plan = "GoodBoy",
            BillingInterval = "Monthly",
            SuccessUrl = "https://example.com/success",
            CancelUrl = "https://example.com/cancel"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WithoutSignature_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/payments/webhook",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Missing X-Signature header.");
    }

        [Fact]
        public async Task Webhook_OrderCreated_RealPayload_PersistsCredits()
        {
                var client = _factory.CreateClient();
                var userId = $"controller-credit-{Guid.NewGuid():N}";
                var json = $$"""
                {
                    "meta": {
                        "event_name": "order_created",
                        "custom_data": {
                            "user_id": "{{userId}}",
                            "type": "credit_purchase",
                            "amount": 10
                        }
                    },
                    "data": {
                        "type": "orders",
                        "id": "{{Guid.NewGuid():N}}",
                        "attributes": {
                            "customer_id": "42",
                            "order_number": "77",
                            "status": "paid",
                            "total": 1859.76,
                            "created_at": "2026-03-10T10:15:30Z"
                        }
                    }
                }
                """;

                var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook")
                {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Signature", ComputeHmacSignature(json, "test-webhook-secret-key"));

                var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<SNIFContext>();
                var balance = await context.CreditBalances.AsNoTracking()
                        .FirstOrDefaultAsync(c => c.UserId == userId);

                balance.Should().NotBeNull();
                balance!.Credits.Should().Be(10);
        }

        [Fact]
        public async Task Webhook_SubscriptionPaymentSuccess_RealPayload_ActivatesExistingSubscription()
        {
            const string subscriptionId = "1741708800789";

                using (var seedScope = _factory.Services.CreateScope())
                {
                        var seedContext = seedScope.ServiceProvider.GetRequiredService<SNIFContext>();
                        seedContext.Subscriptions.Add(new SNIF.Core.Entities.Subscription
                        {
                                Id = Guid.NewGuid().ToString(),
                                UserId = $"user-{Guid.NewGuid():N}",
                                PlanId = SNIF.Core.Enums.SubscriptionPlan.GoodBoy,
                                PaymentProviderSubscriptionId = subscriptionId,
                                Status = SNIF.Core.Enums.SubscriptionStatus.PastDue,
                                CurrentPeriodStart = DateTime.UtcNow.AddDays(-5),
                                CurrentPeriodEnd = DateTime.UtcNow.AddDays(25),
                                CancelAtPeriodEnd = false,
                                CreatedAt = DateTime.UtcNow.AddDays(-30),
                                UpdatedAt = DateTime.UtcNow.AddDays(-1)
                        });
                        await seedContext.SaveChangesAsync();
                }

                var client = _factory.CreateClient();
                var json = $$"""
                {
                    "meta": {
                        "event_name": "subscription_payment_success"
                    },
                    "data": {
                        "type": "subscription-invoices",
                        "id": "{{Guid.NewGuid():N}}",
                        "attributes": {
                            "subscription_id": "{{subscriptionId}}",
                            "customer_id": "555",
                            "status": "paid",
                            "total": 999,
                            "created_at": "2026-03-10T12:00:00Z"
                        }
                    }
                }
                """;

                var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/webhook")
                {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Signature", ComputeHmacSignature(json, "test-webhook-secret-key"));

                var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();

                response.StatusCode.Should().Be(HttpStatusCode.OK, responseBody);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<SNIFContext>();
                var subscription = await context.Subscriptions.AsNoTracking()
                        .FirstAsync(s => s.PaymentProviderSubscriptionId == subscriptionId);

                subscription.Status.Should().Be(SNIF.Core.Enums.SubscriptionStatus.Active);
                subscription.PaymentProviderCustomerId.Should().Be("555");
        }

    [Fact]
    public async Task GetSubscription_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/payments/subscription");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetActivationStatus_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/payments/activation-status");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUsage_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/payments/usage");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CancelSubscription_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/payments/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCreditBalance_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/payments/credits/balance");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PurchaseCredits_InvalidAmount_ReturnsMachineReadableContractError()
    {
        var client = _factory.CreateClient();
        var token = GenerateTestJwt($"credit-contract-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/payments/credits/purchase", new
        {
            amount = 200,
            successUrl = "https://example.com/success"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"invalid_credit_amount\"");
    }

    [Fact]
    public async Task PurchaseCredits_InvalidVariant_ReturnsMachineReadableContractError()
    {
        var client = _factory.CreateClient();
        var token = GenerateTestJwt($"credit-variant-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/payments/credits/purchase", new
        {
            amount = 10,
            variantId = "variant-does-not-exist",
            successUrl = "https://example.com/success"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"invalid_credit_variant\"");
    }

    [Fact]
    public async Task PurchaseCredits_AmountVariantMismatch_ReturnsMachineReadableContractError()
    {
        var client = _factory.CreateClient();
        var token = GenerateTestJwt($"credit-mismatch-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/payments/credits/purchase", new
        {
            amount = 100,
            variantId = "variant-10",
            successUrl = "https://example.com/success"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"credit_amount_variant_mismatch\"");
    }
}
