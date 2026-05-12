using AuthService.Contracts;
using AuthService.Data;
using AuthService.Domain;
using AuthService.Options;
using AuthService.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Application;

public sealed class DuplicateUserException(string email)
    : Exception($"User '{email}' already exists.");

public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException() : base("Invalid credentials.")
    {
    }
}

public sealed class InvalidRefreshTokenException : Exception
{
    public InvalidRefreshTokenException() : base("Invalid refresh token.")
    {
    }
}

public interface IAuthManager
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken);
    Task<UserResponse?> FindUserAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class AuthManager(
    AuthDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> options) : IAuthManager
{
    private readonly JwtOptions _jwtOptions = options.Value;

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var exists = await dbContext.Users.AnyAsync(user => user.Email == email, cancellationToken);
        if (exists)
        {
            throw new DuplicateUserException(email);
        }

        var user = new User
        {
            Email = email,
            PasswordHash = passwordHasher.Hash(request.Password)
        };

        dbContext.Users.Add(user);
        return await PersistTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await dbContext.Users.SingleOrDefaultAsync(item => item.Email == email, cancellationToken);
        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new InvalidCredentialsException();
        }

        return await PersistTokensAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = jwtTokenService.HashRefreshToken(request.RefreshToken);
        var refreshToken = await dbContext.RefreshTokens
            .Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (refreshToken?.User is null || !refreshToken.IsActive(now))
        {
            throw new InvalidRefreshTokenException();
        }

        refreshToken.RevokedAt = now;
        return await PersistTokensAsync(refreshToken.User, cancellationToken);
    }

    public async Task<UserResponse?> FindUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Users
            .Where(user => user.Id == userId)
            .Select(user => new UserResponse(user.Id, user.Email))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<AuthResponse> PersistTokensAsync(User user, CancellationToken cancellationToken)
    {
        var accessToken = jwtTokenService.CreateAccessToken(user);
        var refreshToken = jwtTokenService.CreateRefreshToken();

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = jwtTokenService.HashRefreshToken(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays)
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return new AuthResponse(
            accessToken.Token,
            refreshToken,
            accessToken.ExpiresAt,
            new UserResponse(user.Id, user.Email));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
