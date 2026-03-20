using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Core.Configuration;
using SNIF.Core.Entities;
using SNIF.Core.Enums;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests.Services;

public class LemonSqueezyPaymentServiceTests
{
    private static readonly LemonSqueezyOptions Options = new()
    {
        ApiKey = "test-api-key",
        BaseUrl = "https://api.lemonsqueezy.com",
        StoreId = "42",
        SigningSecret = "test-signing-secret",
        Variants = new LemonSqueezyVariants
        {
            GoodBoyMonthly = "111",
            GoodBoyYearly = "112",
            AlphaPackMonthly = "211",
            AlphaPackYearly = "212",
            TreatBag10 = "1383730",
            TreatBag50 = "1383733",
            TreatBag100 = "1383734"
        }
    };

    private static SNIFContext CreateContext()
    {
        var dbOptions = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase("LsPaymentServiceTests_" + Guid.NewGuid())
            .Options;

        return new SNIFContext(dbOptions);
    }

    [Fact]
    public async Task CreateCheckout_WithoutSuccessUrl_OmitsNullProductOptionFields()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                """
                {
                  "data": {
                    "attributes": {
                      "url": "https://checkout.example/session"
                    }
                  }
                }
                """);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(Options.BaseUrl)
        };
        var client = new LemonSqueezyClient(httpClient, Microsoft.Extensions.Options.Options.Create(Options));

        var url = await client.CreateCheckout("111", new Dictionary<string, string>
        {
            ["user_id"] = "user-1",
            ["plan"] = "GoodBoy"
        });

        url.Should().Be("https://checkout.example/session");
        requestBody.Should().NotBeNull();

        using var json = JsonDocument.Parse(requestBody!);
        var productOptions = json.RootElement
            .GetProperty("data")
            .GetProperty("attributes")
            .GetProperty("product_options");

        productOptions.TryGetProperty("redirect_url", out _).Should().BeFalse();
        productOptions.TryGetProperty("receipt_button_text", out _).Should().BeFalse();
        productOptions.GetProperty("enabled_variants").EnumerateArray().Select(value => value.GetInt32())
            .Should().ContainSingle().Which.Should().Be(111);
    }

    [Fact]
    public async Task CreateCheckout_WhenUpstreamFails_IncludesResponseBodyInException()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"errors\":[{\"detail\":\"redirect_url is invalid\"}]}", Encoding.UTF8, "application/json")
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(Options.BaseUrl)
        };
        var client = new LemonSqueezyClient(httpClient, Microsoft.Extensions.Options.Options.Create(Options));

        var act = () => client.CreateCheckout("111", new Dictionary<string, string>
        {
            ["user_id"] = "user-1"
        });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("422");
        exception.Which.Message.Should().Contain("redirect_url is invalid");
    }

    [Fact]
    public async Task RefreshSubscriptionActivationStatus_LocalProjectionExists_ReturnsActivatedWithoutProviderLookup()
    {
        using var context = CreateContext();
        context.Users.Add(new User
        {
            Id = "user-1",
            UserName = "doglover@example.com",
            Email = "doglover@example.com",
            Name = "Dog Lover",
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        });
        context.Subscriptions.Add(new Subscription
        {
            Id = "local-sub",
            UserId = "user-1",
            PlanId = SubscriptionPlan.GoodBoy,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var requestCounter = 0;
        var service = CreateService(context, new StubHttpHandler(_ =>
        {
            requestCounter++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await service.RefreshSubscriptionActivationStatus("user-1");

        result.State.Should().Be(SubscriptionActivationState.Activated);
        result.Subscription.Should().NotBeNull();
        result.Subscription!.PlanId.Should().Be(SubscriptionPlan.GoodBoy);
        requestCounter.Should().Be(0);
    }

    [Fact]
    public async Task RefreshSubscriptionActivationStatus_WhenProviderIsUnconfigured_ReturnsSafeLocalStateWithoutRemoteLookup()
    {
        using var context = CreateContext();
        context.Users.Add(new User
        {
            Id = "user-unconfigured",
            UserName = "local-only",
            Email = "local-only@example.com",
            Name = "Local Only",
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        });
        await context.SaveChangesAsync();

        var requestCounter = 0;
        var service = CreateService(
            context,
            new StubHttpHandler(_ =>
            {
                requestCounter++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            new LemonSqueezyOptions());

        var result = await service.RefreshSubscriptionActivationStatus("user-unconfigured");

        result.State.Should().Be(SubscriptionActivationState.NotFound);
        result.Subscription.Should().BeNull();
        result.Message.Should().Contain("not configured");
        requestCounter.Should().Be(0);
    }

    [Fact]
    public async Task RefreshSubscriptionActivationStatus_ProviderSubscription_ReconcilesProjection()
    {
        using var context = CreateContext();
        context.Users.Add(new User
        {
            Id = "user-2",
            UserName = "packleader@example.com",
            Email = "packleader@example.com",
            Name = "Pack Leader",
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        });
        context.Subscriptions.Add(new Subscription
        {
            Id = "local-recovery-sub",
            UserId = "user-2",
            PlanId = SubscriptionPlan.GoodBoy,
            PaymentProviderCustomerId = "555",
            Status = SubscriptionStatus.PastDue,
            CurrentPeriodStart = DateTime.UtcNow.AddMonths(-1),
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddMonths(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await context.SaveChangesAsync();

        var renewsAt = new DateTime(2026, 4, 10, 12, 0, 0, DateTimeKind.Utc);
        var createdAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var service = CreateService(context, new StubHttpHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/subscriptions")
            {
                return JsonResponse(
                    $$"""
                    {
                      "data": [
                        {
                          "id": "ls-sub-123",
                          "attributes": {
                            "customer_id": 555,
                            "variant_id": 111,
                            "status": "active",
                            "user_email": "packleader@example.com",
                            "cancelled": false,
                            "created_at": "{{createdAt:O}}",
                            "renews_at": "{{renewsAt:O}}"
                          }
                        }
                      ]
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/orders")
            {
                return JsonResponse("{\"data\":[]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await service.RefreshSubscriptionActivationStatus("user-2");

        result.State.Should().Be(SubscriptionActivationState.Activated);
        result.Subscription.Should().NotBeNull();
        result.Subscription!.PlanId.Should().Be(SubscriptionPlan.GoodBoy);

        var stored = await context.Subscriptions.SingleAsync(s => s.UserId == "user-2");
        stored.PaymentProviderSubscriptionId.Should().Be("ls-sub-123");
        stored.PaymentProviderCustomerId.Should().Be("555");
        stored.CurrentPeriodEnd.Should().Be(renewsAt);
    }

    [Fact]
        public async Task RefreshSubscriptionActivationStatus_EmailOnlyProviderMatch_DoesNotRepairProjection()
        {
                using var context = CreateContext();
                context.Users.Add(new User
                {
                        Id = "user-email-only",
                        UserName = "shared@example.com",
                        Email = "shared@example.com",
                        Name = "Shared Email User",
                        CreatedAt = DateTime.UtcNow,
                        EmailConfirmed = true
                });
                await context.SaveChangesAsync();

                var service = CreateService(context, new StubHttpHandler(request =>
                {
                        if (request.RequestUri?.AbsolutePath == "/v1/subscriptions")
                        {
                                return JsonResponse(
                                        """
                                        {
                                            "data": [
                                                {
                                                    "id": "someone-elses-sub",
                                                    "attributes": {
                                                        "customer_id": 999,
                                                        "variant_id": 111,
                                                        "status": "active",
                                                        "user_email": "shared@example.com",
                                                        "cancelled": false,
                                                        "created_at": "2026-03-10T12:00:00Z",
                                                        "renews_at": "2026-04-10T12:00:00Z"
                                                    }
                                                }
                                            ]
                                        }
                                        """);
                        }

                        if (request.RequestUri?.AbsolutePath == "/v1/orders")
                        {
                                return JsonResponse("{\"data\":[]}");
                        }

                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                }));

                var result = await service.RefreshSubscriptionActivationStatus("user-email-only");

                result.State.Should().Be(SubscriptionActivationState.Pending);
                result.Subscription.Should().BeNull();
                (await context.Subscriptions.CountAsync()).Should().Be(0);
        }

        [Fact]
        public async Task RefreshSubscriptionActivationStatus_StrongProviderMatchWithPausedStatus_ReturnsPending()
        {
                using var context = CreateContext();
                context.Users.Add(new User
                {
                        Id = "user-4",
                        UserName = "paused@example.com",
                        Email = "paused@example.com",
                        Name = "Paused User",
                        CreatedAt = DateTime.UtcNow,
                        EmailConfirmed = true
                });
                context.Subscriptions.Add(new Subscription
                {
                        Id = "paused-local-sub",
                        UserId = "user-4",
                        PlanId = SubscriptionPlan.GoodBoy,
                        PaymentProviderSubscriptionId = "ls-sub-paused",
                        Status = SubscriptionStatus.PastDue,
                        CurrentPeriodStart = DateTime.UtcNow.AddMonths(-1),
                        CurrentPeriodEnd = DateTime.UtcNow.AddDays(-2),
                        CreatedAt = DateTime.UtcNow.AddMonths(-1),
                        UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
                });
                await context.SaveChangesAsync();

                var renewsAt = new DateTime(2026, 4, 12, 12, 0, 0, DateTimeKind.Utc);
                var createdAt = new DateTime(2026, 3, 12, 12, 0, 0, DateTimeKind.Utc);
                var service = CreateService(context, new StubHttpHandler(request =>
                {
                        if (request.RequestUri?.AbsolutePath == "/v1/subscriptions/ls-sub-paused")
                        {
                                return JsonResponse(
                                        $$"""
                                        {
                                            "data": {
                                                "id": "ls-sub-paused",
                                                "attributes": {
                                                    "customer_id": 444,
                                                    "variant_id": 111,
                                                    "status": "paused",
                                                    "user_email": "paused@example.com",
                                                    "cancelled": true,
                                                    "created_at": "{{createdAt:O}}",
                                                    "renews_at": "{{renewsAt:O}}"
                                                }
                                            }
                                        }
                                        """
                                );
                        }

                        if (request.RequestUri?.AbsolutePath == "/v1/orders")
                        {
                                return JsonResponse("{\"data\":[]}");
                        }

                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                }));

                var result = await service.RefreshSubscriptionActivationStatus("user-4");

                result.State.Should().Be(SubscriptionActivationState.Pending);
                result.Subscription.Should().NotBeNull();
                result.Subscription!.Status.Should().Be(SubscriptionStatus.PastDue);

                var stored = await context.Subscriptions.SingleAsync(s => s.UserId == "user-4");
                stored.PaymentProviderSubscriptionId.Should().Be("ls-sub-paused");
                stored.PaymentProviderCustomerId.Should().Be("444");
                stored.Status.Should().Be(SubscriptionStatus.PastDue);
                stored.CurrentPeriodEnd.Should().Be(renewsAt);
        }

        [Fact]
    public async Task RefreshSubscriptionActivationStatus_PaidOrderWithoutSubscription_ReturnsProcessing()
    {
        using var context = CreateContext();
        context.Users.Add(new User
        {
            Id = "user-3",
            UserName = "waiting@example.com",
            Email = "waiting@example.com",
            Name = "Waiting User",
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        });
        await context.SaveChangesAsync();

        var orderCreatedAt = DateTime.UtcNow.AddMinutes(-10);
        var service = CreateService(context, new StubHttpHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/subscriptions")
            {
                return JsonResponse("{\"data\":[]}");
            }

            if (request.RequestUri?.AbsolutePath == "/v1/orders")
            {
                return JsonResponse(
                    $$"""
                    {
                      "data": [
                        {
                          "id": "order-1",
                          "attributes": {
                            "customer_id": 777,
                            "user_email": "waiting@example.com",
                            "status": "paid",
                            "created_at": "{{orderCreatedAt:O}}",
                            "first_order_item": {
                              "variant_id": 111
                            }
                          }
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var result = await service.RefreshSubscriptionActivationStatus("user-3");

        result.State.Should().Be(SubscriptionActivationState.Processing);
        result.Subscription.Should().BeNull();
        (await context.Subscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateCreditPurchaseSession_WithVariantAmountMismatch_ThrowsDeterministicValidationError()
    {
        using var context = CreateContext();

        var service = CreateService(context, new StubHttpHandler(_ => JsonResponse("{}")));

        var act = () => service.CreateCreditPurchaseSession("user-variant-mismatch", new SNIF.Core.DTOs.PurchaseCreditsDto
        {
            Amount = 50,
            VariantId = "1383730"
        });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().StartWith("CREDIT_AMOUNT_VARIANT_MISMATCH:");
    }

    [Fact]
    public async Task CreateCreditPurchaseSession_WithUnknownAmount_ThrowsDeterministicValidationError()
    {
        using var context = CreateContext();

        var service = CreateService(context, new StubHttpHandler(_ => JsonResponse("{}")));

        var act = () => service.CreateCreditPurchaseSession("user-invalid-amount", new SNIF.Core.DTOs.PurchaseCreditsDto
        {
            Amount = 200
        });

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().StartWith("INVALID_CREDIT_AMOUNT:");
    }

    [Fact]
    public async Task CreateCreditPurchaseSession_WithVariantId_UsesVariantMappedAmountInCustomData()
    {
        using var context = CreateContext();
        context.Users.Add(new User
        {
            Id = "user-variant-authoritative",
            UserName = "variant@example.com",
            Email = "variant@example.com",
            Name = "Variant User",
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true
        });
        await context.SaveChangesAsync();

        string? requestBody = null;
        var service = CreateService(context, new StubHttpHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                """
                {
                                    "data": {
                                        "attributes": {
                                            "url": "https://checkout.example/credit"
                    }
                  }
                }
                """);
        }));

        var url = await service.CreateCreditPurchaseSession("user-variant-authoritative", new SNIF.Core.DTOs.PurchaseCreditsDto
        {
            Amount = 10,
            VariantId = "1383730"
        });

        url.Should().Be("https://checkout.example/credit");
        requestBody.Should().NotBeNull();

        using var json = JsonDocument.Parse(requestBody!);
        var attributes = json.RootElement
            .GetProperty("data")
            .GetProperty("attributes");

        attributes.GetProperty("checkout_data")
            .GetProperty("custom")
            .GetProperty("amount")
            .GetString()
            .Should().Be("10");

        json.RootElement
            .GetProperty("data")
            .GetProperty("relationships")
            .GetProperty("variant")
            .GetProperty("data")
            .GetProperty("id")
            .GetString()
            .Should().Be("1383730");
    }

    private static LemonSqueezyPaymentService CreateService(
        SNIFContext context,
        HttpMessageHandler handler,
        LemonSqueezyOptions? options = null)
    {
        options ??= Options;
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        };

        var client = new LemonSqueezyClient(httpClient, Microsoft.Extensions.Options.Options.Create(options));
        var subscriptionService = new SubscriptionService(context);
        var webhookHandler = new LemonSqueezyWebhookHandler(
            Mock.Of<ISubscriptionService>(),
            Mock.Of<IBoostService>(),
            context,
            Microsoft.Extensions.Options.Options.Create(options),
            Mock.Of<ILogger<LemonSqueezyWebhookHandler>>());

        return new LemonSqueezyPaymentService(
            client,
            Microsoft.Extensions.Options.Options.Create(options),
            webhookHandler,
            subscriptionService,
            context);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}