using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Enums;
using SNIF.Core.Interfaces;
using SNIF.Infrastructure.Data;
using System.Security.Claims;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/payments")]
    [EnableRateLimiting("global")]
    public class PaymentController : ControllerBase
    {
        private readonly IPaymentService _paymentService;
        private readonly IEntitlementService _entitlementService;
        private readonly ISubscriptionService _subscriptionService;
        private readonly IUsageService _usageService;
        private readonly SNIFContext _context;
        private const string InvalidCreditAmountCode = "invalid_credit_amount";
        private const string InvalidCreditVariantCode = "invalid_credit_variant";
        private const string CreditAmountVariantMismatchCode = "credit_amount_variant_mismatch";

        public PaymentController(
            IPaymentService paymentService,
            IEntitlementService entitlementService,
            ISubscriptionService subscriptionService,
            IUsageService usageService,
            SNIFContext context)
        {
            _paymentService = paymentService;
            _entitlementService = entitlementService;
            _subscriptionService = subscriptionService;
            _usageService = usageService;
            _context = context;
        }

        private string GetUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("User not authenticated.");

        /// <summary>
        /// Creates a checkout session for subscription purchase.
        /// </summary>
        [Authorize]
        [HttpPost("create-checkout-session")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var userId = GetUserId();
                var url = await _paymentService.CreateCheckoutSession(userId, dto);
                return Ok(new { url });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// LemonSqueezy webhook handler. No auth - verifies via HMAC signature.
        /// </summary>
        [HttpPost("webhook")]
        [AllowAnonymous]
        [DisableRateLimiting]
        public async Task<IActionResult> Webhook()
        {
            var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
            var signature = Request.Headers["X-Signature"].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(signature))
                return BadRequest(new { message = "Missing X-Signature header." });

            try
            {
                await _paymentService.HandleWebhookEvent(json, signature);
                return Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get current user's subscription.
        /// </summary>
        [Authorize]
        [HttpGet("subscription")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(SubscriptionDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetSubscription()
        {
            var userId = GetUserId();
            var sub = await _subscriptionService.GetSubscription(userId);
            var entitlement = await _entitlementService.GetEntitlementAsync(userId);

            if (sub == null)
            {
                return Ok(new SubscriptionDto
                {
                    Id = $"free-{userId}",
                    UserId = userId,
                    PlanId = SubscriptionPlan.Free,
                    Status = SubscriptionStatus.Active,
                    CurrentPeriodStart = entitlement.CurrentPeriodStart ?? DateTime.UtcNow,
                    CurrentPeriodEnd = entitlement.CurrentPeriodEnd ?? DateTime.UtcNow,
                    CancelAtPeriodEnd = false,
                    EffectivePlanId = entitlement.EffectivePlan,
                    EffectiveStatus = entitlement.EffectiveStatus,
                    DowngradeEffectiveAt = entitlement.DowngradeEffectiveAt,
                    EffectiveLimits = entitlement.Limits,
                    IsOverPetLimit = entitlement.IsOverPetLimit,
                    LockedPetCount = entitlement.LockedPets,
                    LockedPets = entitlement.PetStates.Where(p => p.IsLocked).ToArray()
                });
            }

            return Ok(sub with
            {
                EffectivePlanId = entitlement.EffectivePlan,
                EffectiveStatus = entitlement.EffectiveStatus,
                DowngradeEffectiveAt = entitlement.DowngradeEffectiveAt,
                EffectiveLimits = entitlement.Limits,
                IsOverPetLimit = entitlement.IsOverPetLimit,
                LockedPetCount = entitlement.LockedPets,
                LockedPets = entitlement.PetStates.Where(p => p.IsLocked).ToArray()
            });
        }

        [Authorize]
        [HttpGet("entitlements")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(EntitlementSnapshotDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetEntitlements()
        {
            var userId = GetUserId();
            var entitlement = await _entitlementService.GetEntitlementAsync(userId);
            return Ok(entitlement);
        }

        /// <summary>
        /// Attempts to reconcile the current user's subscription against LemonSqueezy.
        /// </summary>
        [Authorize]
        [HttpGet("activation-status")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(SubscriptionActivationStatusDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetActivationStatus()
        {
            var userId = GetUserId();
            var status = await _paymentService.RefreshSubscriptionActivationStatus(userId);
            return Ok(status);
        }

        /// <summary>
        /// Cancel subscription (sets cancel_at_period_end).
        /// </summary>
        [Authorize]
        [HttpPost("cancel")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CancelSubscription()
        {
            try
            {
                var userId = GetUserId();
                await _subscriptionService.CancelSubscription(userId);
                return Ok(new { message = "Subscription will be canceled at the end of the current billing period." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a checkout session for credit purchase.
        /// Credits are only added via webhook when payment completes.
        /// </summary>
        [Authorize]
        [HttpPost("credits/purchase")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> PurchaseCredits([FromBody] PurchaseCreditsDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            try
            {
                var userId = GetUserId();
                var url = await _paymentService.CreateCreditPurchaseSession(userId, dto);
                return Ok(new { url });
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.StartsWith("INVALID_CREDIT_AMOUNT:", StringComparison.Ordinal))
                {
                    return CreditContractError(
                        InvalidCreditAmountCode,
                        ex.Message["INVALID_CREDIT_AMOUNT:".Length..].Trim(),
                        new { requestedAmount = dto.Amount, requestedVariantId = dto.VariantId });
                }

                if (ex.Message.StartsWith("INVALID_CREDIT_VARIANT:", StringComparison.Ordinal))
                {
                    return CreditContractError(
                        InvalidCreditVariantCode,
                        ex.Message["INVALID_CREDIT_VARIANT:".Length..].Trim(),
                        new { requestedAmount = dto.Amount, requestedVariantId = dto.VariantId });
                }

                if (ex.Message.StartsWith("CREDIT_AMOUNT_VARIANT_MISMATCH:", StringComparison.Ordinal))
                {
                    return CreditContractError(
                        CreditAmountVariantMismatchCode,
                        ex.Message["CREDIT_AMOUNT_VARIANT_MISMATCH:".Length..].Trim(),
                        new { requestedAmount = dto.Amount, requestedVariantId = dto.VariantId });
                }

                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// Get credit balance.
        /// </summary>
        [Authorize]
        [HttpGet("credits/balance")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(CreditBalanceDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetCreditBalance()
        {
            var userId = GetUserId();

            var balance = await _context.CreditBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == userId);

            return Ok(new CreditBalanceDto
            {
                Credits = balance?.Credits ?? 0,
                LastPurchasedAt = balance?.LastPurchasedAt
            });
        }

        /// <summary>
        /// Get daily usage stats.
        /// </summary>
        [Authorize]
        [HttpGet("usage")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(UsageResponseDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetUsage([FromQuery] DateTime? date)
        {
            var userId = GetUserId();
            var targetDate = date ?? DateTime.UtcNow;
            var usage = await _usageService.GetDailyUsage(userId, targetDate);
            return Ok(usage);
        }

        /// <summary>
        /// Creates a test checkout session for the cheapest product (TreatBag10, €1.99).
        /// For manual end-to-end payment testing only.
        /// </summary>
        [Authorize]
        [HttpPost("create-test-checkout")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> CreateTestCheckout()
        {
            try
            {
                var userId = GetUserId();

                var dto = new PurchaseCreditsDto { Amount = 10 };
                var url = await _paymentService.CreateCreditPurchaseSession(userId, dto);

                return Ok(new
                {
                    url,
                    product = "TreatBag10",
                    price = "€1.99",
                    note = "This is a test checkout session."
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private BadRequestObjectResult CreditContractError(string code, string message, object details)
        {
            return BadRequest(new
            {
                error = new
                {
                    code,
                    message,
                    details
                }
            });
        }
    }
}
