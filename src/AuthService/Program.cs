using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using AuthService.Application;
using AuthService.Contracts;
using AuthService.Data;
using AuthService.Options;
using AuthService.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

builder.Services.AddProblemDetails();
var authRateLimitPermitLimit = builder.Configuration.GetValue("RateLimiting:Auth:PermitLimit", 30);
var authRateLimitWindowSeconds = builder.Configuration.GetValue("RateLimiting:Auth:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
    {
        var endpoint = httpContext.Request.Path.Value?.ToLowerInvariant() ?? "unknown";
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var partitionKey = $"{endpoint}:{ipAddress}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimitPermitLimit,
            Window = TimeSpan.FromSeconds(authRateLimitWindowSeconds),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (Encoding.UTF8.GetByteCount(jwtOptions.Secret) < 32)
{
    throw new InvalidOperationException("Jwt__Secret must contain at least 32 bytes.");
}

if (builder.Environment.IsProduction() && IsPlaceholderJwtSecret(jwtOptions.Secret))
{
    throw new InvalidOperationException(
        "Production Jwt__Secret must be set to a non-placeholder secret. Replace the default development value before starting auth-service.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? builder.Configuration["DATABASE_URL"]
    ?? "Host=localhost;Port=5433;Database=authdb;Username=auth;Password=auth";
var databaseProvider = builder.Configuration["DatabaseProvider"] ?? "Postgres";

builder.Services.AddDbContext<AuthDbContext>(options =>
{
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString);
        return;
    }

    options.UseNpgsql(connectionString);
});
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthManager, AuthManager>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:3000" };
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), browsing-topics=()";
        headers["X-Frame-Options"] = "DENY";
        return Task.CompletedTask;
    });

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseCors("frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "auth-service" }))
    .WithTags("Health");

var auth = app.MapGroup("/api/auth").WithTags("Authentication").RequireRateLimiting("auth");

auth.MapPost("/register", async (
    [FromBody] RegisterRequest request,
    IAuthManager authManager,
    CancellationToken cancellationToken) =>
{
    var validation = RequestValidator.Validate(request);
    if (validation is not null)
    {
        return validation;
    }

    try
    {
        var response = await authManager.RegisterAsync(request, cancellationToken);
        return Results.Created($"/api/auth/users/{response.User.Id}", response);
    }
    catch (DuplicateUserException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
});

auth.MapPost("/login", async (
    [FromBody] LoginRequest request,
    IAuthManager authManager,
    CancellationToken cancellationToken) =>
{
    var validation = RequestValidator.Validate(request);
    if (validation is not null)
    {
        return validation;
    }

    try
    {
        return Results.Ok(await authManager.LoginAsync(request, cancellationToken));
    }
    catch (InvalidCredentialsException)
    {
        return Results.Unauthorized();
    }
});

auth.MapPost("/refresh", async (
    [FromBody] RefreshTokenRequest request,
    IAuthManager authManager,
    CancellationToken cancellationToken) =>
{
    var validation = RequestValidator.Validate(request);
    if (validation is not null)
    {
        return validation;
    }

    try
    {
        return Results.Ok(await authManager.RefreshAsync(request, cancellationToken));
    }
    catch (InvalidRefreshTokenException)
    {
        return Results.Unauthorized();
    }
});

auth.MapPost("/logout", async (
    [FromBody] RefreshTokenRequest request,
    IAuthManager authManager,
    CancellationToken cancellationToken) =>
{
    var validation = RequestValidator.Validate(request);
    if (validation is not null)
    {
        return validation;
    }

    await authManager.RevokeRefreshTokenAsync(request, cancellationToken);
    return Results.NoContent();
});

auth.MapGet("/me", async (
    ClaimsPrincipal principal,
    IAuthManager authManager,
    CancellationToken cancellationToken) =>
{
    var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(subject, out var userId))
    {
        return Results.Unauthorized();
    }

    var user = await authManager.FindUserAsync(userId, cancellationToken);
    return user is null ? Results.Unauthorized() : Results.Ok(user);
}).RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
    else if (dbContext.Database.IsRelational())
    {
        await dbContext.Database.MigrateAsync();
    }
    else
    {
        await dbContext.Database.EnsureCreatedAsync();
    }
}

app.Run();

public partial class Program
{
    private static bool IsPlaceholderJwtSecret(string secret)
    {
        return string.IsNullOrWhiteSpace(secret)
            || secret.Equals("dev-shared-secret-change-me-with-at-least-32-bytes", StringComparison.Ordinal)
            || secret.Contains("change-me", StringComparison.OrdinalIgnoreCase)
            || secret.Contains("change-this", StringComparison.OrdinalIgnoreCase)
            || secret.Contains("default", StringComparison.OrdinalIgnoreCase)
            || secret.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }
}
