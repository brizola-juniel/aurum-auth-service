using AuthService.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AuthService.Tests;

public sealed class AuthApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new($"Data Source=AuthTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly string _environment;
    private readonly bool _overrideJwtSecret;
    private readonly int _authRateLimitPermitLimit;

    public AuthApiFactory()
        : this("Testing", overrideJwtSecret: true)
    {
    }

    internal AuthApiFactory(
        string environment = "Testing",
        bool overrideJwtSecret = true,
        int authRateLimitPermitLimit = 30)
    {
        _environment = environment;
        _overrideJwtSecret = overrideJwtSecret;
        _authRateLimitPermitLimit = authRateLimitPermitLimit;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("DatabaseProvider", "Sqlite");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connection.ConnectionString);
        if (_overrideJwtSecret)
        {
            builder.UseSetting("Jwt:Secret", "test-secret-with-at-least-thirty-two-bytes-123456");
        }

        builder.UseSetting("Jwt:Issuer", "aurum-auth-service");
        builder.UseSetting("Jwt:Audience", "aurum-reservation-system");
        builder.UseSetting("Jwt:AccessTokenMinutes", "15");
        builder.UseSetting("Jwt:RefreshTokenDays", "7");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", _authRateLimitPermitLimit.ToString());
        builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.DataProtection", "Error");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Update", "Critical");
    }

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
    }

    public new async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
