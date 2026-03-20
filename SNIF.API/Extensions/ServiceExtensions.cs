// SNIF.API/Extensions/ServiceExtensions.cs
using AutoMapper.EquivalencyExpression;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SNIF.Application.Services;
using SNIF.Busniess.Services;
using SNIF.Core.Configuration;
using SNIF.Core.Constants;
using SNIF.Core.Entities;
using SNIF.Core.Interfaces;
using SNIF.Core.Mappings;
using SNIF.Infrastructure.Data;
using SNIF.Infrastructure.Repository;
using SNIF.Infrastructure.Services;
using SNIF.Messaging.Configuration;
using SNIF.Messaging.Services;
using SNIF.SignalR.Services;
using SNIF.Core.Interfaces.Matching;
using SNIF.Core.Models.Matching;
using SNIF.Busniess.Services.Matching;
using SNIF.Busniess.Services.Matching.ScoringFunctions;
using System.Text;
using System.Text.Json.Serialization;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public static class ServiceExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration config)
    {
        InitializeFirebaseAdmin(config);

        // Media storage (Azure Blob or Local fallback)
        var storageProvider = config["Storage:Provider"] ?? "Local";
        if (storageProvider == "Azure")
            services.AddSingleton<IMediaStorageService, AzureBlobStorageService>();
        else
            services.AddSingleton<IMediaStorageService, LocalFileStorageService>();

        // Add AutoMapper
        services.AddAutoMapper(cfg =>
        {
            cfg.AddCollectionMappers();
        }, typeof(LocationMappingProfile).Assembly,
           typeof(PetMappingProfile).Assembly,
           typeof(MatchMappingProfile).Assembly);

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });

        // Core services
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        services.AddSingleton<EmailDeliveryHealthState>();
        services.AddScoped<LoggingAccountEmailService>();
        services.AddScoped<AzureCommunicationAccountEmailService>();
        services.AddScoped<IAccountEmailService>(ResolveAccountEmailService);
        services.AddScoped<IUserService, UserService>();
        services.AddSingleton<IGoogleAuthService, GoogleAuthService>();
        services.AddScoped<IMatchingLogicService, MatchingLogicService>();

        services.AddScoped<IPetService, PetService>();
        services.AddScoped<IMatchService, MatchService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        // Payment & subscription services
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IUsageService, UsageService>();
        services.Configure<LemonSqueezyOptions>(config.GetSection(LemonSqueezyOptions.SectionName));
        services.AddHttpClient<LemonSqueezyClient>();
        services.AddScoped<LemonSqueezyWebhookHandler>();
        services.AddScoped<IPaymentService, LemonSqueezyPaymentService>();
        services.AddScoped<IBoostService, BoostService>();

        // Validate LemonSqueezy configuration on startup
        var lsApiKey = config["LemonSqueezy:ApiKey"];
        if (string.IsNullOrEmpty(lsApiKey))
        {
            Console.WriteLine("WARNING: LemonSqueezy:ApiKey is not configured. Payment features will be unavailable.");
        }
        var lsSigningSecret = config["LemonSqueezy:SigningSecret"];
        if (string.IsNullOrEmpty(lsSigningSecret))
        {
            Console.WriteLine("WARNING: LemonSqueezy:SigningSecret is not configured. Webhooks will fail.");
        }

        // Admin service
        services.AddScoped<IAdminService, AdminService>();

        // Push notification service
        services.AddScoped<IPushNotificationService, PushNotificationService>();

        // Add SignalR services
        services.AddSignalR();
        services.AddScoped<INotificationService, SignalRNotificationService>();
        services.AddScoped<IVideoService, VideoService>();

        // API Explorer for Swagger
        services.AddEndpointsApiExplorer();

        // Messaging configuration (RabbitMQ optional)
        services.Configure<RabbitMQConfig>(config.GetSection("RabbitMQ"));
        var rabbitEnabled = config.GetValue<bool>("RabbitMQ:Enabled");
        if (rabbitEnabled)
        {
            services.AddScoped<IMessagePublisher, RabbitMQPublisher>();
        }
        else
        {
            services.AddScoped<IMessagePublisher, NoopMessagePublisher>();
        }

        // Matchmaking pipeline
        services.AddSingleton(config.GetSection("ScoringWeights").Get<ScoringWeights>() ?? new ScoringWeights());

        services.AddScoped<IMatchScoringFunction, DistanceScorer>();
        services.AddScoped<IMatchScoringFunction, PurposeScorer>();
        services.AddScoped<IMatchScoringFunction, BreedScorer>();
        services.AddScoped<IMatchScoringFunction, PersonalityScorer>();
        services.AddScoped<IMatchScoringFunction, ProfileCompletenessScorer>();
        services.AddScoped<IMatchScoringFunction, HealthScorer>();
        services.AddScoped<IMatchScoringFunction, FreshnessScorer>();
        services.AddScoped<IMatchScoringFunction, ResponseRateScorer>();

        services.AddScoped<IMatchStage, HardFilterStage>();
        services.AddScoped<IMatchStage, ScoringStage>();
        services.AddScoped<IMatchStage, RankingStage>();

        services.AddScoped<IMatchPipeline, MatchPipeline>();

        return services;
    }

    public static IServiceCollection AddSwaggerServices(this IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "SNIF API",
                Version = "v1",
                Description = "Social Network for Furry Friends API"
            });

            // Add JWT Authentication
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement()
            {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    },
                    Scheme = "oauth2",
                    Name = "Bearer",
                    In = ParameterLocation.Header,
                },
                new List<string>()
            }
            });
        });

        return services;
    }

    public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration config)
    {
        var signingKeyBytes = JwtKeyValidator.GetValidatedKeyBytes(config["Jwt:Key"]);

        services.AddIdentityCore<User>(opt =>
        {
            opt.Password.RequireDigit = true;
            opt.Password.RequireLowercase = true;
            opt.Password.RequireUppercase = true;
            opt.Password.RequireNonAlphanumeric = false;
            opt.Password.RequiredLength = 8;
            opt.User.RequireUniqueEmail = true;
            opt.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
        })
        .AddRoles<IdentityRole>()
        .AddEntityFrameworkStores<SNIFContext>()
        .AddSignInManager<SignInManager<User>>();

        // Authorization policies
        services.AddAuthorizationBuilder()
            .AddPolicy("RequireSuperAdmin", policy => policy.RequireRole(AppRoles.SuperAdmin))
            .AddPolicy("RequireAdmin", policy => policy.RequireRole(AppRoles.SuperAdmin, AppRoles.Admin))
            .AddPolicy("RequireStaffRead", policy => policy.RequireRole(
                AppRoles.SuperAdmin,
                AppRoles.Admin,
                AppRoles.Moderator,
                AppRoles.Support))
            .AddPolicy("RequireModerator", policy => policy.RequireRole(AppRoles.SuperAdmin, AppRoles.Admin, AppRoles.Moderator));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = config["Jwt:Issuer"],
                    ValidAudience = config["Jwt:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(signingKeyBytes)
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;

                        if (!string.IsNullOrEmpty(accessToken) &&
                            (path.StartsWithSegments("/matchHub") ||
                             path.StartsWithSegments("/onlineHub") ||
                             path.StartsWithSegments("/chatHub") ||
                             path.StartsWithSegments("/videoHub")))
                        {
                            context.Token = accessToken;
                        }

                        // Fallback: read JWT from httpOnly cookie (web clients)
                        if (string.IsNullOrEmpty(context.Token) &&
                            context.Request.Cookies.TryGetValue("__Host-snif-jwt", out var cookieToken))
                        {
                            context.Token = cookieToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    private static void InitializeFirebaseAdmin(IConfiguration config)
    {
        FirebaseApp? firebaseApp = null;
        try
        {
            firebaseApp = FirebaseApp.DefaultInstance;
        }
        catch (InvalidOperationException)
        {
        }

        if (firebaseApp != null)
        {
            return;
        }

        try
        {
            var credential = ResolveFirebaseCredential(config, out var sourceDescription);
            if (credential == null)
            {
                Console.WriteLine(
                    "WARNING: Firebase Admin SDK credentials were not configured. Set Firebase:AdminCredentialPath, Firebase:AdminCredentialJson, or GOOGLE_APPLICATION_CREDENTIALS. FCM push notifications will be unavailable.");
                return;
            }

            FirebaseApp.Create(new AppOptions
            {
                Credential = credential
            });

            Console.WriteLine($"Firebase Admin SDK initialized using {sourceDescription}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Firebase Admin SDK initialization failed. FCM push notifications will be unavailable. {ex.Message}");
        }
    }

    private static IAccountEmailService ResolveAccountEmailService(IServiceProvider serviceProvider)
    {
        var emailOptions = serviceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AccountEmailServiceConfiguration");

        if (UsesAzureEmailProvider(emailOptions.Provider))
        {
            if (!string.IsNullOrWhiteSpace(emailOptions.ConnectionString) &&
                !string.IsNullOrWhiteSpace(emailOptions.SenderAddress))
            {
                return serviceProvider.GetRequiredService<AzureCommunicationAccountEmailService>();
            }

            var hostEnvironment = serviceProvider.GetService<IHostEnvironment>();
            if (hostEnvironment?.IsProduction() == true)
            {
                throw new InvalidOperationException(
                    "Email provider is configured for Azure Communication in Production, but Email:ConnectionString and/or Email:SenderAddress are missing.");
            }

            logger.LogWarning("Email provider is set to Azure Communication, but Email:ConnectionString and/or Email:SenderAddress are missing. Falling back to logging email delivery.");
            return serviceProvider.GetRequiredService<LoggingAccountEmailService>();
        }

        if (!string.IsNullOrWhiteSpace(emailOptions.Provider) &&
            !emailOptions.Provider.Equals("Logging", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Unknown email provider '{Provider}'. Falling back to logging email delivery.", emailOptions.Provider);
        }

        return serviceProvider.GetRequiredService<LoggingAccountEmailService>();
    }

    private static bool UsesAzureEmailProvider(string? provider)
    {
        return provider != null &&
               (provider.Equals("AzureCommunication", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("AzureCommunicationServices", StringComparison.OrdinalIgnoreCase) ||
                provider.Equals("Azure", StringComparison.OrdinalIgnoreCase));
    }

    private static GoogleCredential? ResolveFirebaseCredential(IConfiguration config, out string sourceDescription)
    {
        var credentialJson = config["Firebase:AdminCredentialJson"];
        if (!string.IsNullOrWhiteSpace(credentialJson))
        {
            sourceDescription = "Firebase:AdminCredentialJson";
            return GoogleCredential.FromJson(credentialJson);
        }

        if (TryLoadFirebaseCredentialFromPath(config["Firebase:AdminCredentialPath"], out var configuredPathCredential))
        {
            sourceDescription = "Firebase:AdminCredentialPath";
            return configuredPathCredential;
        }

        if (TryLoadFirebaseCredentialFromPath(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"), out var googleAppCredential))
        {
            sourceDescription = "GOOGLE_APPLICATION_CREDENTIALS";
            return googleAppCredential;
        }

        foreach (var candidatePath in GetLocalFirebaseCredentialCandidates())
        {
            if (TryLoadFirebaseCredentialFromPath(candidatePath, out var localCredential))
            {
                sourceDescription = $"local file '{candidatePath}'";
                return localCredential;
            }
        }

        sourceDescription = string.Empty;
        return null;
    }

    private static bool TryLoadFirebaseCredentialFromPath(string? credentialPath, out GoogleCredential? credential)
    {
        credential = null;
        if (string.IsNullOrWhiteSpace(credentialPath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(credentialPath);
        if (!File.Exists(normalizedPath))
        {
            return false;
        }

        credential = GoogleCredential.FromFile(normalizedPath);
        return true;
    }

    private static IEnumerable<string> GetLocalFirebaseCredentialCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Directory.GetCurrentDirectory(), "firebase-admin-sdk.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "firebase-admin-sdk.json"),
            Path.Combine(AppContext.BaseDirectory, "firebase-admin-sdk.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "firebase-admin-sdk.json")
        };

        foreach (var candidate in candidates)
        {
            yield return Path.GetFullPath(candidate);
        }
    }
}