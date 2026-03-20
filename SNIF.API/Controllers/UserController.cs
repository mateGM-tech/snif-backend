using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SNIF.Core.DTOs;
using SNIF.Core.Exceptions;
using SNIF.Core.Interfaces;
using Microsoft.Net.Http.Headers;
using SNIF.API.Extensions;
using System.Security.Claims;

namespace SNIF.API.Controllers
{
    [ApiController]
    [Route("api/users")]
    [EnableRateLimiting("auth")]
    public class UserController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly IWebHostEnvironment _environment;

        public UserController(IUserService userService, IWebHostEnvironment environment)
        {
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        private void SetAuthCookie(string token)
        {
            Response.Cookies.Append("__Host-snif-jwt", token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromHours(1),
                IsEssential = true
            });
        }

        private void ClearAuthCookie()
        {
            Response.Cookies.Delete("__Host-snif-jwt", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Path = "/"
            });
        }

        // POST api/users
        [HttpPost]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<AuthResponseDto>> CreateUser(CreateUserDto createUserDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            try
            {
                var user = await _userService.RegisterUserAsync(createUserDto);
                if (!string.IsNullOrEmpty(user.Token))
                    SetAuthCookie(user.Token);
                return CreatedAtAction(nameof(GetUser), new { id = user.Id }, user);
            }
            catch (UnauthorizedAccessException e)
            {
                return BadRequest(new ErrorResponse { Message = e.Message });
            }
            catch (Exception e)
            {
                return BadRequest(new ErrorResponse { Message = e.Message });
            }
        }

        // POST api/users/token
        [HttpPost("token")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<AuthResponseDto>> CreateToken(LoginDto loginDto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid login data" });

            try
            {
                var authResponse = await _userService.LoginUserAsync(loginDto);
                if (!string.IsNullOrEmpty(authResponse.Token))
                    SetAuthCookie(authResponse.Token);
                Response.Headers.Append("Cache-Control", "no-store");
                return Ok(authResponse);
            }
            catch (PendingActivationException e)
            {
                Response.Headers.Append("Cache-Control", "no-store");
                return StatusCode(StatusCodes.Status403Forbidden, e.Response);
            }
            catch (UnauthorizedAccessException e)
            {
                return Unauthorized(new ErrorResponse { Message = e.Message });
            }
        }

        // DELETE api/users/token
        [Authorize]
        [HttpDelete("token")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> DeleteToken()
        {
            await _userService.LogoutUser();
            ClearAuthCookie();
            Response.Headers.Append("Cache-Control", "no-store");
            return NoContent();
        }

        // GET api/users/{id}
        [Authorize]
        [HttpGet("{id}")]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<UserDto>> GetUser(string id)
        {
            try
            {
                var profile = await _userService.GetUserProfileById(id);
                Response.Headers.Append("Cache-Control", "private, max-age=3600");
                return Ok(profile);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // PUT api/users/{id}
        [Authorize]
        [HttpPut("{id}")]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<UserDto>> UpdateUser(string id, UpdateUserDto updateUserDto)
        {
            // Security Fix: Ownership verification to prevent IDOR
            var authUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (authUserId != id)
                return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only modify your own profile" });

            try
            {
                var updatedProfile = await _userService.UpdateUserPersonalInfo(id, updateUserDto);
                Response.Headers.Append("Cache-Control", "no-cache");
                return Ok(updatedProfile);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // PUT api/users/{id}/picture
        [Authorize]
        [HttpPut("{id}/picture")]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<UserDto>> UpdateUserPicture(string id, UpdateProfilePictureDto pictureDto)
        {
            // Security Fix: Ownership verification to prevent IDOR
            var authUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (authUserId != id)
                return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only modify your own profile picture" });

            try
            {
                var updatedProfile = await _userService.UpdateUserProfilePicture(id, pictureDto);
                Response.Headers.Append("Cache-Control", "no-cache");
                return Ok(updatedProfile);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }


        // GET api/users/{id}/picture
        [Authorize]
        [HttpGet("{id}/picture")]
        [ProducesResponseType(typeof(PhysicalFileResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ProfilePictureResponseDto>> GetUserPicture(string id)
        {
            try
            {
                var url = await _userService.GetUserProfilePictureName(id);

                Response.Headers.Append("Cache-Control", "public, max-age=86400");
                return Ok(new ProfilePictureResponseDto { Url = url });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // PUT api/users/{id}/preferences
        [Authorize]
        [HttpPut("{id}/preferences")]
        [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<UserDto>> UpdateUserPreferences(string id, UpdatePreferencesDto preferencesDto)
        {
            // Security Fix: Ownership verification to prevent IDOR
            var authUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (authUserId != id)
                return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse { Message = "You can only modify your own preferences" });

            try
            {
                var updatedUser = await _userService.UpdateUserPreferences(id, preferencesDto);
                Response.Headers.Append("Cache-Control", "no-cache");
                return Ok(updatedUser);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // GET api/users/{id}/preferences
        [Authorize]
        [HttpGet("{id}/preferences")]
        [ProducesResponseType(typeof(PreferencesDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PreferencesDto>> GetUserPreferences(string id)
        {
            try
            {
                var user = await _userService.GetUserProfileById(id);
                Response.Headers.Append("Cache-Control", "private, max-age=300");
                return Ok(user.Preferences ?? new PreferencesDto());
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/token/validate
        [HttpPost("token/validate")]
        [EnableRateLimiting("startupPolling")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<AuthResponseDto>> ValidateToken([FromBody] TokenValidationDto tokenDto)
        {
            try
            {
                var response = await _userService.ValidateAndRefreshTokenAsync(tokenDto.Token);
                if (!string.IsNullOrEmpty(response.Token))
                    SetAuthCookie(response.Token);
                Response.Headers.Append("Cache-Control", "no-store");
                return Ok(response);
            }
            catch (Exception ex)
            {
                return Unauthorized(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/google-auth
        [HttpPost("google-auth")]
        [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
        public async Task<ActionResult<AuthResponseDto>> GoogleAuth([FromBody] GoogleAuthRequestDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            try
            {
                var authResponse = await _userService.GoogleAuthAsync(dto);
                if (!string.IsNullOrEmpty(authResponse.Token))
                    SetAuthCookie(authResponse.Token);
                Response.Headers.Append("Cache-Control", "no-store");
                return Ok(authResponse);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                return Conflict(new ErrorResponse { Message = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unauthorized(new ErrorResponse { Message = ex.Message });
            }
            catch (Exception ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/link-google
        [Authorize]
        [HttpPost("link-google")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
        public async Task<IActionResult> LinkGoogle([FromBody] LinkGoogleAccountDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                await _userService.LinkGoogleAccountAsync(userId, dto);
                return Ok(new { message = "Google account linked successfully" });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/unlink-google
        [Authorize]
        [HttpPost("unlink-google")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> UnlinkGoogle()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                await _userService.UnlinkGoogleAccountAsync(userId);
                return Ok(new { message = "Google account unlinked successfully" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/set-password
        [Authorize]
        [HttpPost("set-password")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> SetPassword([FromBody] SetPasswordDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                await _userService.SetPasswordAsync(userId, dto);
                return Ok(new { message = "Password set successfully" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/forgot-password
        [HttpPost("forgot-password")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            await _userService.ForgotPasswordAsync(dto);
            return Ok(new { message = "If an account exists with this email, you will receive a password reset link." });
        }

        // POST api/users/reset-password
        [HttpPost("reset-password")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            try
            {
                await _userService.ResetPasswordAsync(dto);
                return Ok(new { message = "Password has been reset successfully." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/confirm-email
        [HttpPost("confirm-email")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            try
            {
                await _userService.ConfirmEmailAsync(dto);
                return Ok(new { message = "Email confirmed successfully." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse { Message = ex.Message });
            }
        }

        // POST api/users/resend-confirmation
        [HttpPost("resend-confirmation")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(new ErrorResponse { Message = "Invalid input data" });

            await _userService.ResendConfirmationAsync(dto);
            return Ok(new { message = "If an account exists with this email, a confirmation email will be sent." });
        }

        // GET api/users/me/data-export
        [Authorize]
        [HttpGet("me/data-export")]
        [ProducesResponseType(typeof(UserDataExportDto), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> ExportUserData()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                var exportData = await _userService.ExportUserDataAsync(userId);
                return Ok(exportData);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }

        // DELETE api/users/me
        [Authorize]
        [HttpDelete("me")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> DeleteAccount()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new ErrorResponse { Message = "User not authenticated" });

            try
            {
                await _userService.DeleteAccountAsync(userId);
                return Ok(new { message = "Account deleted successfully" });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ErrorResponse { Message = ex.Message });
            }
        }
    }
}