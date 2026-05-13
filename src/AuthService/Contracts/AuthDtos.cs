using System.ComponentModel.DataAnnotations;

namespace AuthService.Contracts;

public sealed record RegisterRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MinLength(8), MaxLength(128)] string Password);

public sealed record LoginRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MinLength(8), MaxLength(128)] string Password);

public sealed record RefreshTokenRequest([property: Required] string RefreshToken);

public sealed record UserResponse(Guid Id, string Email);

public sealed record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    UserResponse User);
