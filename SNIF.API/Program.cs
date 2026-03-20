using Microsoft.EntityFrameworkCore;
using SNIF.Infrastructure.Data;
using SNIF.SignalR.Hubs;
using SNIF.API.HealthChecks;
using SNIF.API.Middleware;
using System.Collections;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SNIF.Core.Configuration;

static bool IsLoopbackOrigin(string origin)
{
    return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
}

var builder = WebApplication.CreateBuilder(args);

GoogleClientIdValidator.GetValidatedClientIds(
    builder.Configuration["Google:ClientId"],
    builder.Configuration["Google:ClientIdIos"],
    builder.Configuration["Google:ClientIdAndroid"],
    requireAtLeastOneClientId: !builder.Environment.IsEnvironment("Test"));

// Defense-in-depth: global request body size limit (30 MB)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 30_000_000;
});

// Add services
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddSwaggerServices();
builder.Services.AddIdentityServices(builder.Configuration);

// Global exception handling
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Application Insights telemetry
builder.Services.AddApplicationInsightsTelemetry();

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global: 100 requests per minute per IP
    options.AddPolicy("global", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Auth endpoints: 5 requests per minute per IP
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Swipe endpoints: 30 requests per minute per user
    options.AddPolicy("swipe", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Startup/polling-sensitive endpoints: relaxed but still bounded.
    // Prefer user partition when authenticated to avoid NAT/shared-IP false positives.
    options.AddPolicy("startupPolling", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                          ?? context.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Webhook: unlimited
    options.AddPolicy("webhook", context =>
        RateLimitPartition.GetNoLimiter("webhook"));
});

// Static files will use the default web root. We'll ensure required directories exist after building the app.

// Configure PostgreSQL with fallback to App Service connection string env vars
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
    {
        var key = envVar.Key?.ToString() ?? string.Empty;
        if (key.StartsWith("POSTGRESQLCONNSTR_", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = envVar.Value?.ToString();
            break;
        }
        if (connectionString is null && key.StartsWith("CUSTOMCONNSTR_", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback for custom connection strings if provider uses CUSTOMCONNSTR_
            connectionString = envVar.Value?.ToString();
        }
    }
}

builder.Services.AddDbContext<SNIFContext>(options => options.UseNpgsql(connectionString));

// Health checks (registered after connection string is resolved)
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString ?? "", name: "postgresql")
    .AddCheck<LemonSqueezyHealthCheck>("lemonsqueezy")
    .AddCheck<EmailDeliveryHealthCheck>("email_delivery");

// Add CORS - Strict in production, permissive in development
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policyBuilder =>
    {
        if (builder.Environment.IsDevelopment())
        {
            policyBuilder
                .SetIsOriginAllowed(origin => {
                    var uri = new Uri(origin);
                    return uri.Host == "localhost" || uri.Host == "127.0.0.1";
                })
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("Authorization");
        }
        else
        {
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                                ?? Array.Empty<string>();
            allowedOrigins = allowedOrigins
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (allowedOrigins.Length == 0)
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins must contain at least one origin outside Development.");
            }

            if (allowedOrigins.Any(IsLoopbackOrigin))
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins must not include localhost or loopback addresses outside Development.");
            }

            policyBuilder
                .WithOrigins(allowedOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("Authorization");
        }
    });
});

var app = builder.Build();

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Ensure upload directories exist under the resolved web root
var env = app.Environment;
var resolvedWebRoot = env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
Directory.CreateDirectory(Path.Combine(resolvedWebRoot, "uploads", "profiles"));
Directory.CreateDirectory(Path.Combine(resolvedWebRoot, "uploads", "pets", "photos"));
Directory.CreateDirectory(Path.Combine(resolvedWebRoot, "uploads", "pets", "videos"));

// Apply database migrations automatically on startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<SNIFContext>();
    if (context.Database.IsRelational())
    {
        context.Database.Migrate();
    }
    else
    {
        context.Database.EnsureCreated();
    }

    // Seed database with test data if empty
    await DatabaseSeeder.SeedAsync(scope.ServiceProvider);
}

// Production-only security middleware (must come before UseRouting per Microsoft guidelines)
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseExceptionHandler();
app.UseSecurityHeaders();

// Static files served before auth so they don't go through the auth pipeline
app.UseStaticFiles();

app.UseRouting();

// CORS must be between UseRouting and UseEndpoints
app.UseCors("CorsPolicy");

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Use endpoints after all middleware
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
    endpoints.MapHub<MatchHub>("/matchHub").RequireAuthorization();
    endpoints.MapHub<OnlineHub>("/onlineHub").RequireAuthorization();
    endpoints.MapHub<VideoHub>("/videoHub").RequireAuthorization();
    endpoints.MapHub<ChatHub>("/chatHub").RequireAuthorization();
});

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var result = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString()
            })
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(result));
    }
});

app.Run();