using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SNIF.Busniess.Services;
using SNIF.Core.Configuration;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Enums;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;

namespace SNIF.Tests.Services;

public class LemonSqueezyWebhookHandlerTests
{
    private readonly Mock<ISubscriptionService> _subscriptionServiceMock;
    private readonly LemonSqueezyOptions _options;
    private readonly Mock<ILogger<LemonSqueezyWebhookHandler>> _loggerMock;

    public LemonSqueezyWebhookHandlerTests()
    {
        _subscriptionServiceMock = new Mock<ISubscriptionService>();
        _loggerMock = new Mock<ILogger<LemonSqueezyWebhookHandler>>();
        _options = new LemonSqueezyOptions
        {
            SigningSecret = "test-webhook-secret-key",
            ApiKey = "test-key",
            StoreId = "test-store",
            Variants = new LemonSqueezyVariants
            {
                TreatBag10 = "1383730",
                TreatBag50 = "1383733",
                TreatBag100 = "1383734"
            }
        };
    }

    private SNIFContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SNIFContext>()
            .UseInMemoryDatabase("WebhookTestDb_" + Guid.NewGuid())
            .Options;
        return new SNIFContext(options);
    }

    private LemonSqueezyWebhookHandler CreateHandler(SNIFContext context)
    {
        return new LemonSqueezyWebhookHandler(
            _subscriptionServiceMock.Object,
            Mock.Of<IBoostService>(),
            context,
            Options.Create(_options),
            _loggerMock.Object);
    }

    private static string ComputeHmacSignature(string payload, string secret)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds a unique event ID per test to avoid static ConcurrentDictionary collisions.
    /// </summary>
    private static string UniqueId() => Guid.NewGuid().ToString("N")[..12];

    private static string BuildWebhookJson(
        string eventName,
        string? dataId = null,
        Dictionary<string, string>? customData = null,
        string? status = null,
        DateTime? renewsAt = null,
        DateTime? endsAt = null,
        bool? cancelled = null,
        int? customerId = null)
    {
        var payload = new LsWebhookPayload
        {
            Meta = new LsWebhookMeta
            {
                EventName = eventName,
                CustomData = customData is null ? null : JsonSerializer.SerializeToElement(customData)
            },
            Data = new LsWebhookData
            {
                Id = dataId ?? UniqueId(),
                Type = "subscriptions",
                Attributes = new LsWebhookAttributes
                {
                    Status = status,
                    RenewsAt = renewsAt,
                    EndsAt = endsAt,
                    Cancelled = cancelled,
                    CustomerId = customerId.HasValue ? JsonSerializer.SerializeToElement(customerId.Value) : null
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    // ──────────────────────────────────────────────────────
    // 1. HMAC Signature Verification (via LemonSqueezyPaymentService)
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleWebhookEvent_ValidSignature_ProcessesEvent()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var paymentService = new LemonSqueezyPaymentService(
            CreateDummyClient(), Options.Create(_options), handler, _subscriptionServiceMock.Object, context);

        var json = BuildWebhookJson("subscription_created", customData: new()
        {
            ["user_id"] = "user1",
            ["plan"] = "GoodBoy"
        });
        var signature = ComputeHmacSignature(json, _options.SigningSecret);

        await paymentService.HandleWebhookEvent(json, signature);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user1", SubscriptionPlan.GoodBoy, It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleWebhookEvent_InvalidSignature_ThrowsUnauthorized()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var paymentService = new LemonSqueezyPaymentService(
            CreateDummyClient(), Options.Create(_options), handler, _subscriptionServiceMock.Object, context);

        var json = BuildWebhookJson("subscription_created");
        var badSignature = "definitely-not-valid";

        await paymentService.Invoking(s => s.HandleWebhookEvent(json, badSignature))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Invalid webhook signature*");
    }

    [Fact]
    public async Task HandleWebhookEvent_EmptySignature_ThrowsBadRequest()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var paymentService = new LemonSqueezyPaymentService(
            CreateDummyClient(), Options.Create(_options), handler, _subscriptionServiceMock.Object, context);

        var json = BuildWebhookJson("subscription_created");

        await paymentService.Invoking(s => s.HandleWebhookEvent(json, ""))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Missing X-Signature header.*");
    }

    [Fact]
    public async Task HandleWebhookEvent_TamperedPayload_ThrowsUnauthorized()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);
        var paymentService = new LemonSqueezyPaymentService(
            CreateDummyClient(), Options.Create(_options), handler, _subscriptionServiceMock.Object, context);

        var json = BuildWebhookJson("subscription_created");
        var signature = ComputeHmacSignature(json, _options.SigningSecret);

        // Tamper with the payload after signing
        var tamperedJson = json.Replace("subscription_created", "subscription_cancelled");

        await paymentService.Invoking(s => s.HandleWebhookEvent(tamperedJson, signature))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ──────────────────────────────────────────────────────
    // 2. Event Handling: subscription_created
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_CreatesSubscriptionWithCorrectPlan()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var renewsAt = DateTime.UtcNow.AddMonths(1);
        var json = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "user-abc",
                ["plan"] = "AlphaPack"
            },
            renewsAt: renewsAt,
            customerId: 12345);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-abc", SubscriptionPlan.AlphaPack, dataId, "12345"),
            Times.Once);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId,
                SubscriptionStatus.Active,
                It.IsAny<DateTime>(),
                renewsAt,
                false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_GoodBoyPlan_CreatesCorrectly()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "user-dog-lover",
                ["plan"] = "GoodBoy"
            },
            customerId: 999);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-dog-lover", SubscriptionPlan.GoodBoy, dataId, "999"),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_NoRenewsAt_SkipsUpdateCall()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "user1",
                ["plan"] = "GoodBoy"
            });

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);

        // HandleSubscriptionUpdated should NOT be called when RenewsAt is null
        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(It.IsAny<string>(), It.IsAny<SubscriptionStatus>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ──────────────────────────────────────────────────────
    // 3. Event Handling: subscription_updated
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_UpdatesStatusAndPeriod()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var renewsAt = DateTime.UtcNow.AddMonths(1);
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "active",
            renewsAt: renewsAt,
            cancelled: false);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId,
                SubscriptionStatus.Active,
                It.IsAny<DateTime>(),
                renewsAt,
                false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_WithPlanChange_UpdatesPlan()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "active",
            renewsAt: DateTime.UtcNow.AddMonths(1),
            customData: new()
            {
                ["user_id"] = "user-upgrade",
                ["plan"] = "AlphaPack"
            },
            customerId: 555);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-upgrade", SubscriptionPlan.AlphaPack, dataId, "555"),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_CancelAtPeriodEnd_PassesThroughFlag()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var endsAt = DateTime.UtcNow.AddDays(15);
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "active",
            endsAt: endsAt,
            cancelled: true);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId,
                SubscriptionStatus.Active,
                It.IsAny<DateTime>(),
                endsAt,
                true),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────
    // 4. Event Handling: subscription_cancelled
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubscriptionCancelled_SetsStatusCanceled()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var endsAt = DateTime.UtcNow.AddDays(7);
        var json = BuildWebhookJson("subscription_cancelled",
            dataId: dataId,
            endsAt: endsAt);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId,
                SubscriptionStatus.Canceled,
                It.IsAny<DateTime>(),
                endsAt,
                true),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCancelled_NoEndsAt_UsesRenewsAtOrNow()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var renewsAt = DateTime.UtcNow.AddDays(30);
        var json = BuildWebhookJson("subscription_cancelled",
            dataId: dataId,
            renewsAt: renewsAt);

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId,
                SubscriptionStatus.Canceled,
                It.IsAny<DateTime>(),
                renewsAt,
                true),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────
    // 5. Event Handling: subscription_payment_failed
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_PaymentFailed_MarksPastDue()
    {
        using var context = CreateContext();
        var subId = UniqueId();

        context.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "user-pay-fail",
            PlanId = SubscriptionPlan.GoodBoy,
            PaymentProviderSubscriptionId = subId,
            Status = SubscriptionStatus.Active,
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddMonths(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var handler = CreateHandler(context);
        var json = BuildWebhookJson("subscription_payment_failed", dataId: subId);

        await handler.HandleAsync(json);

        var sub = await context.Subscriptions
            .FirstAsync(s => s.PaymentProviderSubscriptionId == subId);
        sub.Status.Should().Be(SubscriptionStatus.PastDue);
        sub.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsync_PaymentFailed_NoMatchingSub_DoesNotThrow()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("subscription_payment_failed", dataId: UniqueId());

        // Should not throw - just no-op when subscription not found
        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();
    }

    // ──────────────────────────────────────────────────────
    // 6. Event Handling: order_created (credit purchase)
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_OrderCreated_CreditPurchase_IncrementsCreditBalance()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "credit-user",
                ["type"] = "credit_purchase",
                ["amount"] = "50"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstOrDefaultAsync(c => c.UserId == "credit-user");
        balance.Should().NotBeNull();
        balance!.Credits.Should().Be(50);
        balance.LastPurchasedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_CreditPurchase_AddsToExistingBalance()
    {
        using var context = CreateContext();
        context.CreditBalances.Add(new CreditBalance
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "existing-credit-user",
            Credits = 20,
            LastPurchasedAt = DateTime.UtcNow.AddDays(-5),
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-5)
        });
        await context.SaveChangesAsync();

        var handler = CreateHandler(context);
        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "existing-credit-user",
                ["type"] = "credit_purchase",
                ["amount"] = "100"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstAsync(c => c.UserId == "existing-credit-user");
        balance.Credits.Should().Be(120); // 20 + 100
    }

        [Fact]
        public async Task HandleAsync_OrderCreated_RealProviderPayload_PersistsCredits()
        {
                using var context = CreateContext();
                var handler = CreateHandler(context);

                var json = $$"""
                {
                    "meta": {
                        "event_name": "order_created",
                        "custom_data": {
                            "user_id": "credit-user-real",
                            "type": "credit_purchase",
                            "amount": 50
                        }
                    },
                    "data": {
                        "type": "orders",
                        "id": "{{UniqueId()}}",
                        "attributes": {
                            "customer_id": "123",
                            "order_number": "456",
                            "status": "paid",
                            "total": 1859.76,
                            "created_at": "2026-03-10T10:15:30Z"
                        }
                    }
                }
                """;

                await handler.HandleAsync(json);

                var balance = await context.CreditBalances
                        .FirstOrDefaultAsync(c => c.UserId == "credit-user-real");
                balance.Should().NotBeNull();
                balance!.Credits.Should().Be(50);
        }

    [Fact]
    public async Task HandleAsync_OrderCreated_NotCreditPurchase_Ignored()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1",
                ["type"] = "subscription",
                ["amount"] = "50"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances.AnyAsync();
        balance.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_ZeroAmount_Ignored()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1",
                ["type"] = "credit_purchase",
                ["amount"] = "0"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances.AnyAsync();
        balance.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_NegativeAmount_Ignored()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1",
                ["type"] = "credit_purchase",
                ["amount"] = "-10"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances.AnyAsync();
        balance.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────
    // 7. Idempotency
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DuplicateEvent_ProcessedOnlyOnce()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "user-idem",
                ["plan"] = "GoodBoy"
            });

        // Process twice
        await handler.HandleAsync(json);
        await handler.HandleAsync(json);

        // Should only call service once
        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-idem", SubscriptionPlan.GoodBoy, dataId, It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DifferentEventsForSameEntity_BothProcessed()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();

        var createdJson = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "user-multi",
                ["plan"] = "GoodBoy"
            });

        var cancelledJson = BuildWebhookJson("subscription_cancelled",
            dataId: dataId,
            endsAt: DateTime.UtcNow.AddDays(30));

        await handler.HandleAsync(createdJson);
        await handler.HandleAsync(cancelledJson);

        // Both events should have been processed (different event names → different keys)
        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), dataId, It.IsAny<string>()),
            Times.Once);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(dataId, SubscriptionStatus.Canceled, It.IsAny<DateTime>(), It.IsAny<DateTime>(), true),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DifferentEntitiesSameEvent_BothProcessed()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId1 = UniqueId();
        var dataId2 = UniqueId();

        var json1 = BuildWebhookJson("subscription_created",
            dataId: dataId1,
            customData: new()
            {
                ["user_id"] = "user-a",
                ["plan"] = "GoodBoy"
            });

        var json2 = BuildWebhookJson("subscription_created",
            dataId: dataId2,
            customData: new()
            {
                ["user_id"] = "user-b",
                ["plan"] = "AlphaPack"
            });

        await handler.HandleAsync(json1);
        await handler.HandleAsync(json2);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-a", SubscriptionPlan.GoodBoy, dataId1, It.IsAny<string>()),
            Times.Once);

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("user-b", SubscriptionPlan.AlphaPack, dataId2, It.IsAny<string>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────
    // 8. Edge Cases
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_UnknownEventType_LogsAndReturns200()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("invoice_paid", dataId: UniqueId());

        // Should not throw - unknown events are silently skipped
        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();

        // No subscription service calls should have been made
        _subscriptionServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_MissingCustomData_LogsWarningAndReturns()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("subscription_created", dataId: UniqueId());

        // No custom_data at all
        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_MissingUserId_LogsWarningAndReturns()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("subscription_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["plan"] = "GoodBoy"
                // missing user_id
            });

        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_MissingPlan_LogsWarningAndReturns()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("subscription_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1"
                // missing plan
            });

        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionCreated_InvalidPlanValue_LogsWarningAndReturns()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("subscription_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1",
                ["plan"] = "SuperDuperPlan" // not a valid enum
            });

        await handler.Invoking(h => h.HandleAsync(json))
            .Should().NotThrowAsync();

        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription(It.IsAny<string>(), It.IsAny<SubscriptionPlan>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_MissingUserId_Ignored()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["type"] = "credit_purchase",
                ["amount"] = "50"
            });

        await handler.HandleAsync(json);

        var anyBalance = await context.CreditBalances.AnyAsync();
        anyBalance.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_InvalidAmount_Ignored()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "user1",
                ["type"] = "credit_purchase",
                ["amount"] = "not-a-number"
            });

        await handler.HandleAsync(json);

        var anyBalance = await context.CreditBalances.AnyAsync();
        anyBalance.Should().BeFalse();
    }

        [Fact]
        public async Task HandleAsync_SubscriptionPaymentSuccess_RealProviderInvoicePayload_ActivatesExistingSubscription()
        {
            const string subscriptionId = "1741708800123";
                using var context = CreateContext();
                context.Subscriptions.Add(new Subscription
                {
                        Id = Guid.NewGuid().ToString(),
                        UserId = "invoice-user",
                        PlanId = SubscriptionPlan.GoodBoy,
                PaymentProviderSubscriptionId = subscriptionId,
                        Status = SubscriptionStatus.PastDue,
                        CurrentPeriodStart = DateTime.UtcNow.AddDays(-10),
                        CurrentPeriodEnd = DateTime.UtcNow.AddDays(20),
                        CancelAtPeriodEnd = false,
                        CreatedAt = DateTime.UtcNow.AddDays(-30),
                        UpdatedAt = DateTime.UtcNow.AddDays(-1)
                });
                await context.SaveChangesAsync();

                var handler = CreateHandler(context);
                var json = """
                {
                    "meta": {
                        "event_name": "subscription_payment_success"
                    },
                    "data": {
                        "type": "subscription-invoices",
                        "id": "inv_123",
                        "attributes": {
                            "subscription_id": "1741708800123",
                            "customer_id": "12345",
                            "status": "paid",
                            "total": 999,
                            "created_at": "2026-03-10T12:00:00Z"
                        }
                    }
                }
                """;

                await handler.Invoking(h => h.HandleAsync(json))
                        .Should().NotThrowAsync();

                var subscription = await context.Subscriptions
                        .FirstAsync(s => s.PaymentProviderSubscriptionId == subscriptionId);
                subscription.Status.Should().Be(SubscriptionStatus.Active);
                subscription.PaymentProviderCustomerId.Should().Be("12345");
        }

    [Fact]
    public async Task HandleAsync_SubscriptionPaymentSuccess_RealReplayShape_ReactivatesPastDueSubscription()
    {
        const string subscriptionId = "1741708800456";

        using var context = CreateContext();
        context.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid().ToString(),
            UserId = "replay-user",
            PlanId = SubscriptionPlan.GoodBoy,
            PaymentProviderSubscriptionId = subscriptionId,
            PaymentProviderCustomerId = "900777",
            Status = SubscriptionStatus.PastDue,
            CurrentPeriodStart = DateTime.UtcNow.AddDays(-5),
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
            CancelAtPeriodEnd = false,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            UpdatedAt = DateTime.UtcNow.AddDays(-1)
        });
        await context.SaveChangesAsync();

        var handler = CreateHandler(context);
        var json = $$"""
        {
            "meta": {
                "event_name": "subscription_payment_success"
            },
            "data": {
                "type": "subscription-invoices",
                "id": "1741708800457",
                "attributes": {
                    "subscription_id": "{{subscriptionId}}",
                    "customer_id": 900777,
                    "status": "paid",
                    "total": 999,
                    "created_at": "2026-03-10T13:20:00Z"
                }
            }
        }
        """;

        await handler.HandleAsync(json);

        var subscription = await context.Subscriptions
            .FirstAsync(s => s.PaymentProviderSubscriptionId == subscriptionId);
        subscription.Status.Should().Be(SubscriptionStatus.Active);
        subscription.PaymentProviderCustomerId.Should().Be("900777");
    }

    [Fact]
    public async Task HandleAsync_InvalidJson_Throws()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        await handler.Invoking(h => h.HandleAsync("not-json"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Failed to deserialize webhook payload:*");
    }

    [Fact]
    public async Task HandleAsync_NullPayload_ThrowsInvalidOperation()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        // JSON "null" deserializes to null
        await handler.Invoking(h => h.HandleAsync("null"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deserialize*");
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_MapsPastDueStatus()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "past_due",
            renewsAt: DateTime.UtcNow.AddDays(3));

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId, SubscriptionStatus.PastDue,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_MapsTrialingStatus()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "on_trial",
            renewsAt: DateTime.UtcNow.AddDays(14));

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId, SubscriptionStatus.Trialing,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_MapsExpiredToCanceled()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "expired",
            endsAt: DateTime.UtcNow.AddDays(-1));

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId, SubscriptionStatus.Canceled,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_SubscriptionUpdated_MapsPausedToPastDue()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_updated",
            dataId: dataId,
            status: "paused",
            renewsAt: DateTime.UtcNow.AddDays(30));

        await handler.HandleAsync(json);

        _subscriptionServiceMock.Verify(
            s => s.HandleSubscriptionUpdated(
                dataId, SubscriptionStatus.PastDue,
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), false),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentDuplicateEvents_OnlyOneProcessed()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var dataId = UniqueId();
        var json = BuildWebhookJson("subscription_created",
            dataId: dataId,
            customData: new()
            {
                ["user_id"] = "concurrent-user",
                ["plan"] = "GoodBoy"
            });

        // Fire multiple concurrent calls
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => handler.HandleAsync(json))
            .ToArray();

        await Task.WhenAll(tasks);

        // Should have been called exactly once despite concurrent attempts
        _subscriptionServiceMock.Verify(
            s => s.CreateOrUpdateSubscription("concurrent-user", SubscriptionPlan.GoodBoy, dataId, It.IsAny<string>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────
    // 9. Credit Amount Variant Validation
    // ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1383730, 10)]
    [InlineData(1383733, 50)]
    [InlineData(1383734, 100)]
    public async Task HandleAsync_OrderCreated_WithVariantId_UsesValidatedCreditAmount(int variantId, int expectedCredits)
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        var json = $$"""
        {
            "meta": {
                "event_name": "order_created",
                "custom_data": {
                    "user_id": "variant-user",
                    "type": "credit_purchase",
                    "amount": {{expectedCredits}}
                }
            },
            "data": {
                "type": "orders",
                "id": "{{UniqueId()}}",
                "attributes": {
                    "variant_id": {{variantId}},
                    "customer_id": "100",
                    "status": "paid"
                }
            }
        }
        """;

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstOrDefaultAsync(c => c.UserId == "variant-user");
        balance.Should().NotBeNull();
        balance!.Credits.Should().Be(expectedCredits);
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_VariantMismatch_UsesVariantAmountNotClaimed()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        // custom_data claims 100, but variant_id maps to 10
        var json = $$"""
        {
            "meta": {
                "event_name": "order_created",
                "custom_data": {
                    "user_id": "mismatch-user",
                    "type": "credit_purchase",
                    "amount": 100
                }
            },
            "data": {
                "type": "orders",
                "id": "{{UniqueId()}}",
                "attributes": {
                    "variant_id": 1383730,
                    "customer_id": "100",
                    "status": "paid"
                }
            }
        }
        """;

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstOrDefaultAsync(c => c.UserId == "mismatch-user");
        balance.Should().NotBeNull();
        balance!.Credits.Should().Be(10, "variant 1383730 maps to 10 credits, not the claimed 100");
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_UnknownVariant_IsRejectedAndDoesNotGrantCredits()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        // variant_id 9999 doesn't match any configured credit variant
        var json = $$"""
        {
            "meta": {
                "event_name": "order_created",
                "custom_data": {
                    "user_id": "fallback-user",
                    "type": "credit_purchase",
                    "amount": 50
                }
            },
            "data": {
                "type": "orders",
                "id": "{{UniqueId()}}",
                "attributes": {
                    "variant_id": 9999,
                    "customer_id": "100",
                    "status": "paid"
                }
            }
        }
        """;

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstOrDefaultAsync(c => c.UserId == "fallback-user");
        balance.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_OrderCreated_NoVariantId_FallsBackToClaimedAmount()
    {
        using var context = CreateContext();
        var handler = CreateHandler(context);

        // No variant_id in payload at all — backward compatibility
        var json = BuildWebhookJson("order_created",
            dataId: UniqueId(),
            customData: new()
            {
                ["user_id"] = "no-variant-user",
                ["type"] = "credit_purchase",
                ["amount"] = "50"
            });

        await handler.HandleAsync(json);

        var balance = await context.CreditBalances
            .FirstOrDefaultAsync(c => c.UserId == "no-variant-user");
        balance.Should().NotBeNull();
        balance!.Credits.Should().Be(50);
    }

    // ──────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a LemonSqueezyClient with a dummy HttpClient.
    /// Only needed for PaymentService instantiation — webhook path doesn't call the client.
    /// </summary>
    private LemonSqueezyClient CreateDummyClient()
    {
        var httpClient = new HttpClient(new DummyHttpHandler())
        {
            BaseAddress = new Uri("https://api.lemonsqueezy.com")
        };

        var clientOptions = Options.Create(new LemonSqueezyOptions
        {
            ApiKey = "test-api-key",
            StoreId = "test-store-id",
            BaseUrl = "https://api.lemonsqueezy.com"
        });

        return new LemonSqueezyClient(httpClient, clientOptions);
    }

    private class DummyHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
