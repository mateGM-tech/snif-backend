using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SNIF.Core.Configuration;
using SNIF.Core.DTOs;
using SNIF.Core.Enums;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;

namespace SNIF.Busniess.Services
{
    public class LemonSqueezyPaymentService : IPaymentService
    {
        private static readonly TimeSpan ProcessingWindow = TimeSpan.FromHours(2);

        private readonly LemonSqueezyClient _client;
        private readonly LemonSqueezyOptions _options;
        private readonly LemonSqueezyWebhookHandler _webhookHandler;
        private readonly ISubscriptionService _subscriptionService;
        private readonly SNIFContext _context;
        private readonly Dictionary<int, string> _creditVariantByAmount;
        private readonly Dictionary<string, int> _creditAmountByVariant;

        public LemonSqueezyPaymentService(
            LemonSqueezyClient client,
            IOptions<LemonSqueezyOptions> options,
            LemonSqueezyWebhookHandler webhookHandler,
            ISubscriptionService subscriptionService,
            SNIFContext context)
        {
            _client = client;
            _options = options.Value;
            _webhookHandler = webhookHandler;
            _subscriptionService = subscriptionService;
            _context = context;

            _creditVariantByAmount = new Dictionary<int, string>();
            _creditAmountByVariant = new Dictionary<string, int>(StringComparer.Ordinal);

            AddCreditPack(10, _options.Variants.TreatBag10);
            AddCreditPack(50, _options.Variants.TreatBag50);
            AddCreditPack(100, _options.Variants.TreatBag100);
        }

        public async Task<string> CreateCheckoutSession(string userId, CreateCheckoutSessionDto dto)
        {
            var variantId = GetVariantId(dto.Plan, dto.BillingInterval);

            var customData = new Dictionary<string, string>
            {
                ["user_id"] = userId,
                ["plan"] = dto.Plan.ToString(),
                ["billing_interval"] = dto.BillingInterval.ToString()
            };

            var user = await _context.Users.FindAsync(userId);
            return await _client.CreateCheckout(variantId, customData, dto.SuccessUrl, user?.Email, user?.Name);
        }

        public async Task<string> CreateCreditPurchaseSession(string userId, PurchaseCreditsDto dto)
        {
            var (resolvedAmount, variantId) = ResolveCreditPurchaseSelection(dto);

            var customData = new Dictionary<string, string>
            {
                ["user_id"] = userId,
                ["type"] = "credit_purchase",
                ["amount"] = resolvedAmount.ToString()
            };

            var user = await _context.Users.FindAsync(userId);
            return await _client.CreateCheckout(variantId, customData, dto.SuccessUrl, user?.Email, user?.Name);
        }

        private void AddCreditPack(int amount, string? configuredVariantId)
        {
            if (string.IsNullOrWhiteSpace(configuredVariantId))
                return;

            var trimmed = configuredVariantId.Trim();
            _creditVariantByAmount[amount] = trimmed;
            _creditAmountByVariant[trimmed] = amount;
        }

        private (int Amount, string VariantId) ResolveCreditPurchaseSelection(PurchaseCreditsDto dto)
        {
            var requestedVariantId = dto.VariantId?.Trim();

            if (!string.IsNullOrWhiteSpace(requestedVariantId))
            {
                if (!_creditAmountByVariant.TryGetValue(requestedVariantId, out var mappedAmount))
                {
                    throw new InvalidOperationException(
                        $"INVALID_CREDIT_VARIANT: Variant '{requestedVariantId}' is not configured for credit purchases.");
                }

                if (dto.Amount > 0 && dto.Amount != mappedAmount)
                {
                    throw new InvalidOperationException(
                        $"CREDIT_AMOUNT_VARIANT_MISMATCH: Requested amount {dto.Amount} does not match variant '{requestedVariantId}'. Expected {mappedAmount}.");
                }

                return (mappedAmount, _creditVariantByAmount[mappedAmount]);
            }

            if (!_creditVariantByAmount.TryGetValue(dto.Amount, out var variantId))
            {
                throw new InvalidOperationException(
                    $"INVALID_CREDIT_AMOUNT: Credit amount {dto.Amount} is not configured for purchase.");
            }

            return (dto.Amount, variantId);
        }

        public Task<string> CreatePortalSession(string userId)
        {
            throw new NotSupportedException(
                "LemonSqueezy manages billing directly. Use the customer portal URL from the subscription.");
        }

        public async Task<SubscriptionActivationStatusDto> RefreshSubscriptionActivationStatus(string userId)
        {
            var localSubscription = await _subscriptionService.GetSubscription(userId);
            if (IsActivatedSubscription(localSubscription))
            {
                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.Activated,
                    Message = "Subscription is already active.",
                    Subscription = localSubscription
                };
            }

            if (!IsRemoteLookupConfigured())
            {
                if (localSubscription != null)
                {
                    return CreateActivationStatus(
                        localSubscription,
                        activatedMessage: "Subscription is already active.",
                        pendingMessage: "Subscription exists locally, but provider reconciliation is unavailable in this environment.");
                }

                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.NotFound,
                    Message = "Payment provider is not configured in this environment. No active subscription was found locally."
                };
            }

            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == userId);

            if (string.IsNullOrWhiteSpace(user?.Email))
            {
                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.Pending,
                    Message = "Waiting for subscription details to become available."
                };
            }

            var latestLocalRecord = await _context.Subscriptions
                .AsNoTracking()
                .Where(subscription => subscription.UserId == userId)
                .OrderByDescending(subscription => subscription.UpdatedAt)
                .FirstOrDefaultAsync();

            var localProviderSubscriptionId = latestLocalRecord?.PaymentProviderSubscriptionId;
            var localProviderCustomerId = latestLocalRecord?.PaymentProviderCustomerId;
            IReadOnlyList<LsSubscriptionData> providerSubscriptions = [];

            var providerSubscription = await ResolveRecoverableSubscription(
                user.Email,
                localProviderSubscriptionId,
                localProviderCustomerId,
                subscriptions => providerSubscriptions = subscriptions);

            if (providerSubscription != null && TryMapPlan(providerSubscription.Attributes.VariantId, out var plan))
            {
                var providerStatus = MapLsStatus(providerSubscription.Attributes.Status);

                await _subscriptionService.CreateOrUpdateSubscription(
                    userId,
                    plan,
                    providerSubscription.Id,
                    providerSubscription.Attributes.CustomerId?.ToString());

                await _subscriptionService.HandleSubscriptionUpdated(
                    providerSubscription.Id,
                    providerStatus,
                    providerSubscription.Attributes.CreatedAt ?? DateTime.UtcNow,
                    providerSubscription.Attributes.RenewsAt
                        ?? providerSubscription.Attributes.EndsAt
                        ?? DateTime.UtcNow.AddMonths(1),
                    providerSubscription.Attributes.Cancelled);

                var refreshedSubscription = await _subscriptionService.GetSubscription(userId);
                if (refreshedSubscription != null)
                {
                    return CreateActivationStatus(
                        refreshedSubscription,
                        activatedMessage: "Subscription activated successfully.",
                        pendingMessage: "Subscription found, but it is not currently active.");
                }

                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.NotFound,
                    Message = "Subscription found, but it is not active anymore."
                };
            }

            if (providerSubscriptions.Count == 0)
            {
                providerSubscriptions = await _client.ListSubscriptionsByEmail(user.Email);
            }

            if (HasUnverifiedEmailOnlySubscription(providerSubscriptions, localProviderSubscriptionId, localProviderCustomerId))
            {
                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.Pending,
                    Message = "A matching subscription was found, but ownership could not be verified yet."
                };
            }

            var providerOrders = await _client.ListOrdersByEmail(user.Email);

            var recentPaidOrder = providerOrders
                .Where(order => IsSubscriptionOrder(order.Attributes.FirstOrderItem?.VariantId))
                .Where(order => string.Equals(order.Attributes.Status, "paid", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(order => order.Attributes.CreatedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            if (recentPaidOrder?.Attributes.CreatedAt is DateTime orderCreatedAt
                && orderCreatedAt >= DateTime.UtcNow.Subtract(ProcessingWindow))
            {
                return new SubscriptionActivationStatusDto
                {
                    State = SubscriptionActivationState.Processing,
                    Message = "Payment received. Subscription activation is still processing."
                };
            }

            if (localSubscription != null)
            {
                return CreateActivationStatus(
                    localSubscription,
                    activatedMessage: "Subscription is already active.",
                    pendingMessage: "Subscription exists locally, but it is not currently active.");
            }

            return new SubscriptionActivationStatusDto
            {
                State = SubscriptionActivationState.NotFound,
                Message = "No active subscription was found for this payment yet."
            };
        }

        public async Task HandleWebhookEvent(string json, string signature)
        {
            if (string.IsNullOrWhiteSpace(signature))
                throw new InvalidOperationException("Missing X-Signature header.");

            if (!VerifySignature(json, signature, _options.SigningSecret))
                throw new UnauthorizedAccessException("Invalid webhook signature.");

            await _webhookHandler.HandleAsync(json);
        }

        private static bool VerifySignature(string payload, string signature, string secret)
        {
            if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret))
                return false;

            var secretBytes = Encoding.UTF8.GetBytes(secret);
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            using var hmac = new HMACSHA256(secretBytes);
            var computedHash = hmac.ComputeHash(payloadBytes);
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedSignature),
                Encoding.UTF8.GetBytes(signature));
        }

        private string GetVariantId(SubscriptionPlan plan, BillingInterval interval)
        {
            var variantId = (plan, interval) switch
            {
                (SubscriptionPlan.GoodBoy, BillingInterval.Monthly) => _options.Variants.GoodBoyMonthly,
                (SubscriptionPlan.GoodBoy, BillingInterval.Yearly) => _options.Variants.GoodBoyYearly,
                (SubscriptionPlan.AlphaPack, BillingInterval.Monthly) => _options.Variants.AlphaPackMonthly,
                (SubscriptionPlan.AlphaPack, BillingInterval.Yearly) => _options.Variants.AlphaPackYearly,
                _ => throw new InvalidOperationException($"No variant for plan: {plan}, interval: {interval}")
            };

            if (string.IsNullOrEmpty(variantId))
                throw new InvalidOperationException($"Variant ID not configured for {plan}/{interval}");

            return variantId;
        }

        public async Task<string> CreateDayPassCheckoutSession(string userId, CreateDayPassCheckoutDto dto)
        {
            var variantId = GetDayPassVariantId(dto.BoostType, dto.DurationDays);

            var customData = new Dictionary<string, string>
            {
                ["user_id"] = userId,
                ["type"] = "day_pass",
                ["boost_type"] = dto.BoostType.ToString(),
                ["duration_days"] = dto.DurationDays.ToString()
            };

            var user = await _context.Users.FindAsync(userId);
            return await _client.CreateCheckout(variantId, customData, dto.SuccessUrl, user?.Email, user?.Name);
        }

        private string GetDayPassVariantId(BoostType boostType, int durationDays)
        {
            var variantId = (boostType, durationDays) switch
            {
                (BoostType.Radius50, 1) => _options.Variants.DayPassRadius50_1,
                (BoostType.Radius50, 3) => _options.Variants.DayPassRadius50_3,
                (BoostType.Radius50, 7) => _options.Variants.DayPassRadius50_7,
                (BoostType.VideoChat, 1) => _options.Variants.DayPassVideoChat_1,
                (BoostType.VideoChat, 3) => _options.Variants.DayPassVideoChat_3,
                (BoostType.VideoChat, 7) => _options.Variants.DayPassVideoChat_7,
                _ => throw new InvalidOperationException($"No variant for day pass: {boostType}, {durationDays} days")
            };

            if (string.IsNullOrEmpty(variantId))
                throw new InvalidOperationException($"Day pass variant ID not configured for {boostType}/{durationDays}d");

            return variantId;
        }

        private async Task<LsSubscriptionData?> ResolveRecoverableSubscription(
            string email,
            string? localSubscriptionId,
            string? localCustomerId,
            Action<IReadOnlyList<LsSubscriptionData>> cacheSubscriptions)
        {
            if (!string.IsNullOrWhiteSpace(localSubscriptionId))
            {
                var directMatch = await _client.TryGetSubscription(localSubscriptionId);
                if (directMatch != null && IsSubscriptionVariant(directMatch.Attributes.VariantId))
                {
                    return directMatch;
                }
            }

            if (string.IsNullOrWhiteSpace(localSubscriptionId) && string.IsNullOrWhiteSpace(localCustomerId))
            {
                return null;
            }

            var subscriptions = await _client.ListSubscriptionsByEmail(email);
            cacheSubscriptions(subscriptions);

            return subscriptions
                .Where(subscription => IsSubscriptionVariant(subscription.Attributes.VariantId))
                .Where(subscription => IsStrongOwnershipMatch(subscription, localSubscriptionId, localCustomerId))
                .OrderByDescending(subscription => string.Equals(subscription.Id, localSubscriptionId, StringComparison.Ordinal))
                .ThenByDescending(subscription => string.Equals(subscription.Attributes.CustomerId?.ToString(), localCustomerId, StringComparison.Ordinal))
                .ThenByDescending(subscription => subscription.Attributes.UpdatedAt ?? subscription.Attributes.CreatedAt ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        private bool IsSubscriptionVariant(int variantId) =>
            TryMapPlan(variantId, out _);

        private static bool IsActivatedSubscription(SubscriptionDto? subscription) =>
            subscription is { PlanId: not SubscriptionPlan.Free }
            && (subscription.Status == SubscriptionStatus.Active || subscription.Status == SubscriptionStatus.Trialing);

        private static SubscriptionActivationStatusDto CreateActivationStatus(
            SubscriptionDto subscription,
            string activatedMessage,
            string pendingMessage) => new()
        {
            State = IsActivatedSubscription(subscription)
                ? SubscriptionActivationState.Activated
                : SubscriptionActivationState.Pending,
            Message = IsActivatedSubscription(subscription)
                ? activatedMessage
                : pendingMessage,
            Subscription = subscription
        };

        private static bool IsStrongOwnershipMatch(
            LsSubscriptionData subscription,
            string? localSubscriptionId,
            string? localCustomerId) =>
            (!string.IsNullOrWhiteSpace(localSubscriptionId)
             && string.Equals(subscription.Id, localSubscriptionId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(localCustomerId)
                && string.Equals(subscription.Attributes.CustomerId?.ToString(), localCustomerId, StringComparison.Ordinal));

        private bool HasUnverifiedEmailOnlySubscription(
            IReadOnlyList<LsSubscriptionData> subscriptions,
            string? localSubscriptionId,
            string? localCustomerId) =>
            subscriptions.Any(subscription =>
                IsSubscriptionVariant(subscription.Attributes.VariantId)
                && RequiresOwnershipVerification(subscription.Attributes.Status)
                && !IsStrongOwnershipMatch(subscription, localSubscriptionId, localCustomerId));

        private bool IsSubscriptionOrder(int? variantId) =>
            variantId.HasValue && IsSubscriptionVariant(variantId.Value);

        private bool TryMapPlan(int variantId, out SubscriptionPlan plan)
        {
            if (VariantMatches(_options.Variants.GoodBoyMonthly, variantId)
                || VariantMatches(_options.Variants.GoodBoyYearly, variantId))
            {
                plan = SubscriptionPlan.GoodBoy;
                return true;
            }

            if (VariantMatches(_options.Variants.AlphaPackMonthly, variantId)
                || VariantMatches(_options.Variants.AlphaPackYearly, variantId))
            {
                plan = SubscriptionPlan.AlphaPack;
                return true;
            }

            plan = SubscriptionPlan.Free;
            return false;
        }

        private static bool VariantMatches(string configuredVariantId, int providerVariantId) =>
            int.TryParse(configuredVariantId, out var parsedVariantId) && parsedVariantId == providerVariantId;

        private bool IsRemoteLookupConfigured() =>
            !string.IsNullOrWhiteSpace(_options.ApiKey)
            && HasAnySubscriptionVariantConfigured();

        private bool HasAnySubscriptionVariantConfigured() =>
            !string.IsNullOrWhiteSpace(_options.Variants.GoodBoyMonthly)
            || !string.IsNullOrWhiteSpace(_options.Variants.GoodBoyYearly)
            || !string.IsNullOrWhiteSpace(_options.Variants.AlphaPackMonthly)
            || !string.IsNullOrWhiteSpace(_options.Variants.AlphaPackYearly);

        private static bool RequiresOwnershipVerification(string? status) => status?.ToLowerInvariant() switch
        {
            "active" => true,
            "on_trial" => true,
            "past_due" => true,
            "paused" => true,
            "unpaid" => true,
            _ => false
        };

        private static SubscriptionStatus MapLsStatus(string? status) => status?.ToLowerInvariant() switch
        {
            "active" => SubscriptionStatus.Active,
            "on_trial" => SubscriptionStatus.Trialing,
            "past_due" => SubscriptionStatus.PastDue,
            "paused" => SubscriptionStatus.PastDue,
            "cancelled" => SubscriptionStatus.Canceled,
            "expired" => SubscriptionStatus.Canceled,
            "unpaid" => SubscriptionStatus.PastDue,
            _ => SubscriptionStatus.PastDue
        };
    }
}
