using AutoMapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SNIF.Core.Constants;
using SNIF.Core.DTOs;
using SNIF.Core.Entities;
using SNIF.Core.Exceptions;
using SNIF.Core.Interfaces;
using SNIF.Core.Models;
using SNIF.Core.Utilities;
using SNIF.Infrastructure.Data;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace SNIF.Busniess.Services
{
    public class UserService : IUserService
    {
        private readonly ITokenService _tokenService;
        private readonly SignInManager<User> _signInManager;
        private readonly UserManager<User> _userManager;
        private readonly SNIFContext _context;
        private readonly IWebHostEnvironment _environment;
        private readonly IMapper _mapper;
        private readonly IMediaStorageService _mediaStorage;
        private readonly IGoogleAuthService _googleAuthService;
        private readonly IEntitlementService _entitlementService;
        private readonly IAccountEmailService _accountEmailService;

        public UserService(ITokenService tokenService, SignInManager<User> signInManager, UserManager<User> userManager, SNIFContext context, IWebHostEnvironment environment, IMapper mapper, IMediaStorageService mediaStorage, IGoogleAuthService googleAuthService, IEntitlementService entitlementService, IAccountEmailService accountEmailService)
        {
            _tokenService = tokenService;
            _signInManager = signInManager;
            _userManager = userManager;
            _context = context;
            _environment = environment;
            _mapper = mapper;
            _mediaStorage = mediaStorage;
            _googleAuthService = googleAuthService;
            _entitlementService = entitlementService;
            _accountEmailService = accountEmailService;
        }
        public async Task<AuthResponseDto> RegisterUserAsync(CreateUserDto createUserDto)
        {
            if (await _userManager.FindByEmailAsync(createUserDto.Email) != null)
            {
                throw new Exception("Email already registered");
            }

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                Location? location = null;
                if (createUserDto.Location != null)
                {
                    location = _mapper.Map<Location>(createUserDto.Location);
                    location.CreatedAt = DateTime.UtcNow;
                    _context.Locations.Add(location);
                    await _context.SaveChangesAsync();
                }

                var user = new User
                {
                    Email = createUserDto.Email,
                    UserName = GenerateInternalUsername(),
                    Name = createUserDto.Name,
                    Location = location,
                    CreatedAt = DateTime.UtcNow,
                    EmailConfirmed = false,
                    EmailConfirmationToken = GenerateSecureToken(),
                    Preferences = new UserPreferences
                    {
                        SearchRadius = 50,
                        ShowOnlineStatus = true,
                        CreatedAt = DateTime.UtcNow,
                        NotificationSettings = new NotificationSettings
                        {
                            CreatedAt = DateTime.UtcNow,
                            EmailNotifications = true,
                            PushNotifications = true,
                        }
                    }
                };

                var result = await _userManager.CreateAsync(user, createUserDto.Password);
                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    throw new Exception($"Failed to create user: {errors}");
                }

                await transaction.CommitAsync();
                await _accountEmailService.SendEmailConfirmationAsync(user, user.EmailConfirmationToken!);

                return BuildPendingActivationResponse(
                    user,
                    "Account created. Check your email to activate your account.");
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        public async Task<UserDto> GetUserProfileById(string userId)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Pets)
                .Include(u => u.Preferences)
                    .ThenInclude(p => p.NotificationSettings)
                .FirstOrDefaultAsync(u => u.Id == userId) ?? throw new KeyNotFoundException("User not found");
            var userDto = await EnrichUserDtoAsync(user, _mapper.Map<UserDto>(user));
            return userDto ?? throw new InvalidOperationException("Failed to map user to DTO");
        }

        public async Task<UserDto> IsUserLoggedInByEmail(string email)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Preferences)
                .FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
                throw new UnauthorizedAccessException("User not found");

            var isSignedIn = _signInManager.IsSignedIn(new ClaimsPrincipal(
                new ClaimsIdentity(new[] { new Claim(ClaimTypes.Email, email) })
            ));

            if (!isSignedIn)
                throw new UnauthorizedAccessException("User is not logged in");

            var userDto = await EnrichUserDtoAsync(user, _mapper.Map<UserDto>(user));
            return userDto ?? throw new InvalidOperationException("Failed to map user to DTO");
        }


        public async Task<AuthResponseDto> LoginUserAsync(LoginDto loginDto)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .FirstOrDefaultAsync(u => u.Email == loginDto.Email);

            if (user == null)
                throw new UnauthorizedAccessException("Invalid email or password");

            var result = await _signInManager.CheckPasswordSignInAsync(user, loginDto.Password, false);
            if (!result.Succeeded)
                throw new UnauthorizedAccessException("Invalid email or password");

            EnsureConfirmedLocalAccount(user);

            if (loginDto.Location != null)
                await UpdateUserLocation(user.Id, loginDto.Location);

            return await BuildAuthenticatedAuthResponseAsync(user);
        }

        public async Task LogoutUser()
        {
            // JWT tokens are stateless - logout is handled client-side
            // by clearing the stored token
            await Task.CompletedTask;
        }

        public async Task<UserDto> UpdateUserPersonalInfo(string userId, UpdateUserDto updateUserPersonalInfoDto)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Pets)
                .Include(u => u.Preferences)
                    .ThenInclude(p => p.NotificationSettings)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new KeyNotFoundException("User not found");

            user.Name = updateUserPersonalInfoDto.Name;

            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await EnrichUserDtoAsync(user, _mapper.Map<UserDto>(user))
                ?? throw new InvalidOperationException("Failed to map user to DTO");
        }

        public async Task<UserDto> UpdateUserProfilePicture(string userId, UpdateProfilePictureDto pictureDto)
        {
            if (string.IsNullOrEmpty(_environment.WebRootPath))
                throw new InvalidOperationException("WebRootPath is not configured");

            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Pets)
                .Include(u => u.Preferences)
                    .ThenInclude(p => p.NotificationSettings)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new KeyNotFoundException("User not found");

            // Delete old profile picture if exists
            if (!string.IsNullOrEmpty(user.ProfilePicturePath))
            {
                await _mediaStorage.DeleteAsync(user.ProfilePicturePath);
            }

            // Save new profile picture
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(pictureDto.FileName)}";
            var imageBytes = Convert.FromBase64String(pictureDto.Base64Data);
            using var stream = new MemoryStream(imageBytes);
            var storedUrl = await _mediaStorage.UploadAsync(stream, fileName, "image/" + Path.GetExtension(pictureDto.FileName).TrimStart('.'));

            user.ProfilePicturePath = storedUrl;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await EnrichUserDtoAsync(user, _mapper.Map<UserDto>(user))
                ?? throw new InvalidOperationException("Failed to map user to DTO");
        }

        private async Task ValidateProfilePicture(UpdateProfilePictureDto pictureDto)
        {
            if (string.IsNullOrEmpty(pictureDto.Base64Data))
                throw new ArgumentException("Profile picture data is required");

            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png" };
            var extension = Path.GetExtension(pictureDto.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
                throw new ArgumentException($"Invalid file type. Allowed types are: {string.Join(", ", allowedExtensions)}");

            var maxSizeInBytes = 5 * 1024 * 1024;
            var sizeInBytes = pictureDto.Base64Data.Length * 3 / 4;

            if (sizeInBytes > maxSizeInBytes)
                throw new ArgumentException($"File size exceeds maximum allowed size of {maxSizeInBytes / 1024 / 1024}MB");
        }

        public async Task<string> GetUserProfilePictureName(string id)
        {
            var user = await _userManager.Users
                .FirstOrDefaultAsync(u => u.Id == id)
                ?? throw new KeyNotFoundException("User not found");

            if (string.IsNullOrEmpty(user.ProfilePicturePath))
                throw new KeyNotFoundException("Profile picture not found");

            return MediaPathResolver.ResolveProfilePicturePath(user.ProfilePicturePath)
                ?? throw new KeyNotFoundException("Profile picture not found");
        }

        public async Task<UserDto> UpdateUserPreferences(string userId, UpdatePreferencesDto preferencesDto)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Preferences)
                    .ThenInclude(p => p.NotificationSettings)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new KeyNotFoundException("User not found");

            if (user.Preferences == null)
            {
                user.Preferences = _mapper.Map<UserPreferences>(preferencesDto);
                if (user.Preferences != null)
                {
                    user.Preferences.UserId = userId;
                    user.Preferences.SearchRadius = await GetClampedSearchRadiusAsync(userId, preferencesDto.SearchRadius);
                    // Ensure NotificationSettings always exists to satisfy FK
                    if (user.Preferences.NotificationSettings == null)
                    {
                        user.Preferences.NotificationSettings = new NotificationSettings
                        {
                            CreatedAt = DateTime.UtcNow,
                            EmailNotifications = true,
                            PushNotifications = true,
                            NewMatchNotifications = true,
                            MessageNotifications = true,
                            BreedingRequestNotifications = true,
                            PlaydateRequestNotifications = true,
                        };
                    }
                }
            }
            else
            {
                _mapper.Map(preferencesDto, user.Preferences);
                user.Preferences.SearchRadius = await GetClampedSearchRadiusAsync(userId, preferencesDto.SearchRadius);
            }

            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await EnrichUserDtoAsync(user, _mapper.Map<UserDto>(user)) ?? throw new InvalidOperationException("Failed to map user to DTO");
        }

        public async Task<AuthResponseDto> ValidateAndRefreshTokenAsync(string token)
        {
            var principal = _tokenService.ValidateToken(token);
            if (principal == null)
                throw new UnauthorizedAccessException("Invalid token");

            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
                throw new UnauthorizedAccessException("Invalid token claims");

            var user = await _userManager.Users
                .Include(u => u.Location)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new UnauthorizedAccessException("User not found");

            return await BuildAuthenticatedAuthResponseAsync(user);
        }

        public async Task<UserDto> UpdateUserLocation(string userId, LocationDto locationDto)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
                throw new KeyNotFoundException("User not found");

            if (user.Location == null)
            {
                user.Location = _mapper.Map<Location>(locationDto);
                if (user.Location != null)
                {
                    user.Location.CreatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                _mapper.Map(locationDto, user.Location);
            }

            await _context.SaveChangesAsync();
            return _mapper.Map<UserDto>(user) ?? throw new InvalidOperationException("Failed to map user to DTO");
        }

        

        public async Task UpdateUserOnlineStatus(string userId, bool isOnline)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                user.LastSeen = DateTime.UtcNow;
                user.IsOnline = isOnline;
                await _userManager.UpdateAsync(user);
            }
        }

        public async Task<AuthResponseDto> GoogleAuthAsync(GoogleAuthRequestDto request)
        {
            var googleUser = await _googleAuthService.ValidateGoogleTokenAsync(request.IdToken);

            // Look up by GoogleSubjectId first
            var user = await _userManager.Users
                .Include(u => u.Location)
                .FirstOrDefaultAsync(u => u.GoogleSubjectId == googleUser.SubjectId);

            if (user != null)
            {
                if (user.IsBanned)
                    throw new UnauthorizedAccessException("Account is banned");
                if (user.SuspendedUntil.HasValue && user.SuspendedUntil > DateTime.UtcNow)
                    throw new UnauthorizedAccessException("Account is suspended");

                if (request.Location != null)
                    await UpdateUserLocation(user.Id, request.Location);

                return await BuildAuthenticatedAuthResponseAsync(user);
            }

            // Check if email is already registered
            var existingByEmail = await _userManager.FindByEmailAsync(googleUser.Email);
            if (existingByEmail != null)
            {
                throw new InvalidOperationException(
                    "An account with this email already exists. Please sign in with your password and link your Google account in Settings.");
            }

            // New user — create account without password
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                Location? location = null;
                if (request.Location != null)
                {
                    location = _mapper.Map<Location>(request.Location);
                    location.CreatedAt = DateTime.UtcNow;
                    _context.Locations.Add(location);
                    await _context.SaveChangesAsync();
                }

                var newUser = new User
                {
                    Email = googleUser.Email,
                    UserName = GenerateInternalUsername(),
                    Name = googleUser.Name,
                    GoogleSubjectId = googleUser.SubjectId,
                    ProfilePicturePath = googleUser.PictureUrl,
                    Location = location,
                    CreatedAt = DateTime.UtcNow,
                    EmailConfirmed = true,
                    Preferences = new UserPreferences
                    {
                        SearchRadius = 50,
                        ShowOnlineStatus = true,
                        CreatedAt = DateTime.UtcNow,
                        NotificationSettings = new NotificationSettings
                        {
                            CreatedAt = DateTime.UtcNow,
                            EmailNotifications = true,
                            PushNotifications = true,
                        }
                    }
                };

                var result = await _userManager.CreateAsync(newUser);
                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    throw new Exception($"Failed to create user: {errors}");
                }

                await transaction.CommitAsync();

                return await BuildAuthenticatedAuthResponseAsync(newUser);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task LinkGoogleAccountAsync(string userId, LinkGoogleAccountDto request)
        {
            var googleUser = await _googleAuthService.ValidateGoogleTokenAsync(request.IdToken);

            // Check if this Google account is already linked to another user
            var existingLinked = await _userManager.Users
                .FirstOrDefaultAsync(u => u.GoogleSubjectId == googleUser.SubjectId);
            if (existingLinked != null)
                throw new InvalidOperationException("This Google account is already linked to another user.");

            var user = await _userManager.FindByIdAsync(userId)
                ?? throw new KeyNotFoundException("User not found");

            user.GoogleSubjectId = googleUser.SubjectId;
            user.UpdatedAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
        }

        public async Task UnlinkGoogleAccountAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId)
                ?? throw new KeyNotFoundException("User not found");

            if (string.IsNullOrEmpty(user.PasswordHash))
                throw new InvalidOperationException("Cannot unlink Google account without a password set. Please set a password first.");

            user.GoogleSubjectId = null;
            user.UpdatedAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
        }

        public async Task SetPasswordAsync(string userId, SetPasswordDto request)
        {
            var user = await _userManager.FindByIdAsync(userId)
                ?? throw new KeyNotFoundException("User not found");

            if (!string.IsNullOrEmpty(user.PasswordHash))
                throw new InvalidOperationException("Password is already set. Use change password instead.");

            var result = await _userManager.AddPasswordAsync(user, request.NewPassword);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to set password: {errors}");
            }
        }

        public async Task ForgotPasswordAsync(ForgotPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return; // Don't reveal whether email exists

            user.PasswordResetToken = GenerateSecureToken();
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await _userManager.UpdateAsync(user);
            await _accountEmailService.SendPasswordResetAsync(user, user.PasswordResetToken);
        }

        public async Task ResetPasswordAsync(ResetPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                throw new InvalidOperationException("Invalid reset request");

            if (string.IsNullOrEmpty(user.PasswordResetToken) ||
                user.PasswordResetToken != dto.Token)
                throw new InvalidOperationException("Invalid or expired reset token");

            if (!user.PasswordResetTokenExpiry.HasValue ||
                user.PasswordResetTokenExpiry.Value < DateTime.UtcNow)
                throw new InvalidOperationException("Invalid or expired reset token");

            // Remove existing password if set, then add new one
            if (!string.IsNullOrEmpty(user.PasswordHash))
            {
                await _userManager.RemovePasswordAsync(user);
            }
            var result = await _userManager.AddPasswordAsync(user, dto.NewPassword);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Failed to reset password: {errors}");
            }

            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            await _userManager.UpdateAsync(user);
        }

        public async Task ConfirmEmailAsync(ConfirmEmailDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                throw new InvalidOperationException("Invalid confirmation request");

            if (string.IsNullOrEmpty(user.EmailConfirmationToken) ||
                user.EmailConfirmationToken != dto.Token)
                throw new InvalidOperationException("Invalid confirmation token");

            user.EmailConfirmed = true;
            user.EmailConfirmationToken = null;
            await _userManager.UpdateAsync(user);
        }

        public async Task ResendConfirmationAsync(ResendConfirmationDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return; // Don't reveal whether email exists

            if (user.EmailConfirmed)
                return; // Already confirmed

            user.EmailConfirmationToken = GenerateSecureToken();
            await _userManager.UpdateAsync(user);
            await _accountEmailService.SendEmailConfirmationAsync(user, user.EmailConfirmationToken);
        }

        private async Task<double> GetClampedSearchRadiusAsync(string userId, double requestedRadius)
        {
            var entitlement = await _entitlementService.GetEntitlementAsync(userId);
            return Math.Min(requestedRadius, entitlement.Limits.SearchRadiusKm);
        }

        private async Task<UserDto?> EnrichUserDtoAsync(User user, UserDto? userDto)
        {
            if (userDto == null)
                return null;

            var entitlement = await _entitlementService.GetEntitlementAsync(user.Id);
            var preferences = userDto.Preferences == null
                ? null
                : userDto.Preferences with
                {
                    SearchRadius = Math.Min(userDto.Preferences.SearchRadius, entitlement.Limits.SearchRadiusKm)
                };

            var pets = userDto.Pets
                .Select(pet =>
                {
                    var petState = entitlement.PetStates.FirstOrDefault(state => state.PetId == pet.Id);
                    return pet with
                    {
                        IsLocked = petState?.IsLocked ?? false,
                        EntitlementLockReason = petState?.LockReason
                    };
                })
                .ToArray();

            return userDto with
            {
                Preferences = preferences,
                Pets = pets
            };
        }

        private async Task<AuthResponseDto> BuildAuthenticatedAuthResponseAsync(User user)
        {
            var authResponse = _mapper.Map<AuthResponseDto>(user)!;
            var primaryRole = await ResolvePrimaryRoleAsync(user);

            return authResponse with
            {
                Role = primaryRole,
                Token = await _tokenService.CreateTokenAsync(user),
                AuthStatus = "Authenticated",
                RequiresEmailConfirmation = false,
                CanResendConfirmation = false,
                Message = null
            };
        }

        private async Task<string?> ResolvePrimaryRoleAsync(User user)
        {
            var roles = await _userManager.GetRolesAsync(user);

            foreach (var role in AppRoles.All)
            {
                if (roles.Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    return role;
                }
            }

            return roles.FirstOrDefault();
        }

        private AuthResponseDto BuildPendingActivationResponse(User user, string message)
        {
            var authResponse = _mapper.Map<AuthResponseDto>(user)!;
            return authResponse with
            {
                Token = null,
                EmailConfirmed = false,
                AuthStatus = "PendingActivation",
                RequiresEmailConfirmation = true,
                CanResendConfirmation = true,
                Message = message
            };
        }

        private void EnsureConfirmedLocalAccount(User user)
        {
            if (user.EmailConfirmed || string.IsNullOrEmpty(user.PasswordHash))
                return;

            throw new PendingActivationException(BuildPendingActivationResponse(
                user,
                "Email confirmation is required before you can sign in."));
        }

        private static string GenerateSecureToken()
        {
            var tokenBytes = new byte[32];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(tokenBytes);
            return Convert.ToBase64String(tokenBytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private static string GenerateInternalUsername() => $"usr_{Guid.NewGuid():N}";

        public async Task<UserDataExportDto> ExportUserDataAsync(string userId)
        {
            var user = await _userManager.Users
                .Include(u => u.Location)
                .Include(u => u.Pets).ThenInclude(p => p.Media)
                .Include(u => u.Pets).ThenInclude(p => p.Location)
                .Include(u => u.Preferences)
                    .ThenInclude(p => p!.NotificationSettings)
                .FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new KeyNotFoundException("User not found");

            var petIds = user.Pets.Select(p => p.Id).ToList();

            var matches = await _context.Matches
                .Include(m => m.InitiatiorPet)
                .Include(m => m.TargetPet)
                .Where(m => petIds.Contains(m.InitiatiorPetId) || petIds.Contains(m.TargetPetId))
                .ToListAsync();

            var messages = await _context.Messages
                .Where(m => m.SenderId == userId || m.ReceiverId == userId)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            var subscriptions = await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var usageRecords = await _context.UsageRecords
                .Where(u => u.UserId == userId)
                .OrderByDescending(u => u.Date)
                .ToListAsync();

            var reports = await _context.Reports
                .Where(r => r.ReporterId == userId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return new UserDataExportDto
            {
                ExportDate = DateTime.UtcNow,
                UserId = userId,
                Profile = new UserProfileExportDto
                {
                    Name = user.Name,
                    Email = user.Email ?? string.Empty,
                    CreatedAt = user.CreatedAt,
                    UpdatedAt = user.UpdatedAt,
                    IsOnline = user.IsOnline,
                    LastSeen = user.LastSeen,
                    ProfilePicturePath = user.ProfilePicturePath,
                    Location = user.Location != null ? _mapper.Map<LocationDto>(user.Location) : null,
                    Preferences = user.Preferences != null ? _mapper.Map<PreferencesDto>(user.Preferences) : null,
                },
                Pets = _mapper.Map<ICollection<PetDto>>(user.Pets),
                Matches = _mapper.Map<ICollection<MatchDto>>(matches),
                Messages = _mapper.Map<ICollection<MessageDto>>(messages),
                Subscriptions = _mapper.Map<ICollection<SubscriptionDto>>(subscriptions),
                Usage = usageRecords.Select(u => new UsageExportDto
                {
                    Type = u.Type.ToString(),
                    Count = u.Count,
                    Date = u.Date,
                }).ToList(),
                Reports = reports.Select(r => new ReportExportDto
                {
                    Id = r.Id,
                    TargetUserId = r.TargetUserId,
                    Reason = r.Reason.ToString(),
                    Description = r.Description,
                    Status = r.Status.ToString(),
                    CreatedAt = r.CreatedAt,
                }).ToList(),
            };
        }

        public async Task DeleteAccountAsync(string userId)
        {
            var user = await _userManager.Users
                .Include(u => u.Pets).ThenInclude(p => p.Media)
                .FirstOrDefaultAsync(u => u.Id == userId)
                ?? throw new KeyNotFoundException("User not found");

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Cancel active subscriptions
                var activeSubscriptions = await _context.Subscriptions
                    .Where(s => s.UserId == userId && s.Status == Core.Enums.SubscriptionStatus.Active)
                    .ToListAsync();
                foreach (var sub in activeSubscriptions)
                {
                    sub.Status = Core.Enums.SubscriptionStatus.Canceled;
                    sub.CancelAtPeriodEnd = true;
                }

                // Delete uploaded media
                foreach (var pet in user.Pets)
                {
                    foreach (var media in pet.Media)
                    {
                        if (!string.IsNullOrEmpty(media.FileName))
                            await _mediaStorage.DeleteAsync(media.FileName);
                    }
                }
                if (!string.IsNullOrEmpty(user.ProfilePicturePath))
                    await _mediaStorage.DeleteAsync(user.ProfilePicturePath);

                // Anonymize user data
                var anonymizedEmail = $"deleted_{Guid.NewGuid():N}@snif.app";
                user.Email = anonymizedEmail;
                user.NormalizedEmail = anonymizedEmail.ToUpperInvariant();
                user.UserName = $"deleted_{user.Id}";
                user.NormalizedUserName = user.UserName.ToUpperInvariant();
                user.Name = "Deleted User";
                user.ProfilePicturePath = null;
                user.PhoneNumber = null;
                user.GoogleSubjectId = null;
                user.PasswordHash = null;
                user.SecurityStamp = Guid.NewGuid().ToString();
                user.IsDeleted = true;
                user.DeletedAt = DateTime.UtcNow;
                user.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }
}
