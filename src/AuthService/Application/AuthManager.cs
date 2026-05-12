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
    Task RevokeRefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken);
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

        try
        {
            dbContext.Users.Add(user);
            return await PersistTokensAsync(user, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            throw new DuplicateUserException(email);
        }
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
        var now = DateTimeOffset.UtcNow;

        var refreshToken = await dbContext.RefreshTokens
            .AsNoTracking()
            .Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (refreshToken?.User is null)
        {
            throw new InvalidRefreshTokenException();
        }

        if (refreshToken.RevokedAt is not null)
        {
            await RevokeActiveRefreshTokensForUserAsync(refreshToken.UserId, now, cancellationToken);
            throw new InvalidRefreshTokenException();
        }

        if (!refreshToken.IsActive(now))
        {
            throw new InvalidRefreshTokenException();
        }

        var revokedTokens = await dbContext.RefreshTokens
            .Where(token => token.TokenHash == tokenHash && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, now),
                cancellationToken);

        if (revokedTokens != 1)
        {
            throw new InvalidRefreshTokenException();
        }

        return await PersistTokensAsync(refreshToken.User, cancellationToken);
    }

    public async Task RevokeRefreshTokenAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = jwtTokenService.HashRefreshToken(request.RefreshToken);
        var now = DateTimeOffset.UtcNow;

        await dbContext.RefreshTokens
            .Where(token => token.TokenHash == tokenHash && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, now),
                cancellationToken);
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

    private async Task RevokeActiveRefreshTokensForUserAsync(
        Guid userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, revokedAt),
                cancellationToken);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("23505", StringComparison.OrdinalIgnoreCase);
    }
}
