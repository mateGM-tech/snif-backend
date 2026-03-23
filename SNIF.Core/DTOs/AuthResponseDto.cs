namespace SNIF.Core.DTOs
{
    public record AuthResponseDto
    {
        public string Id { get; init; } = null!;
        public string Email { get; init; } = null!;
        public string Name { get; init; } = null!;
        public string? Role { get; init; }
        public string? Token { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool EmailConfirmed { get; init; }
        public string AuthStatus { get; init; } = "Authenticated";
        public bool RequiresEmailConfirmation { get; init; }
        public bool CanResendConfirmation { get; init; }
        public string? Message { get; init; }
    }
}