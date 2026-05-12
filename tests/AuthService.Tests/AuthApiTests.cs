using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthService.Contracts;

namespace AuthService.Tests;

public sealed class AuthApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HealthIncludesEnterpriseSecurityHeaders()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("camera=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task RegisterIssuesJwtWithRequiredClaimsAndAllowsMe()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            $"dev-{Guid.NewGuid():N}@aurum.test",
            "StrongPass123!"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.AccessToken);
        Assert.NotEmpty(payload.RefreshToken);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(payload.AccessToken);
        Assert.Equal(payload.User.Id.ToString(), jwt.Subject);
        Assert.Contains(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Email && claim.Value == payload.User.Email);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);
        var me = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task RegisterRejectsDuplicateEmails()
    {
        var email = $"duplicate-{Guid.NewGuid():N}@aurum.test";
        var request = new RegisterRequest(email, "StrongPass123!");

        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/auth/register", request)).StatusCode);
    }

    [Fact]
    public async Task RegisterAndLoginTreatEmailCaseInsensitively()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var mixedCaseEmail = $"Case-{suffix}@Aurum.Test";
        var normalizedEmail = mixedCaseEmail.ToLowerInvariant();

        var register = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            mixedCaseEmail,
            "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var payload = await register.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.Equal(normalizedEmail, payload!.User.Email);

        var duplicate = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            normalizedEmail,
            "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(
            mixedCaseEmail.ToUpperInvariant(),
            "StrongPass123!"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicateRegistrationReturnsConflictInsteadOfServerError()
    {
        using var factory = new AuthApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        try
        {
            var request = new RegisterRequest($"race-{Guid.NewGuid():N}@aurum.test", "StrongPass123!");
            var attempts = await Task.WhenAll(
                Enumerable.Range(0, 6).Select(_ => client.PostAsJsonAsync("/api/auth/register", request)));

            Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(5, attempts.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            Assert.DoesNotContain(attempts, response => response.StatusCode == HttpStatusCode.InternalServerError);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegisterRejectsInvalidEmailAndShortPassword()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            "not-an-email",
            "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginRejectsInvalidEmailAndShortPassword()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(
            "not-an-email",
            "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginRejectsWrongPassword()
    {
        var email = $"login-{Guid.NewGuid():N}@aurum.test";
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "StrongPass123!"));

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPass123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestingEnvironmentDoesNotExposeOpenApi()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ProductionRejectsDefaultJwtSecret()
    {
        using var factory = new AuthApiFactory("Production", overrideJwtSecret: false);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Production Jwt__Secret must be set to a non-placeholder secret", exception.ToString());
    }

    [Fact]
    public async Task RefreshTokenRotatesAndRevokesOldToken()
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            $"refresh-{Guid.NewGuid():N}@aurum.test",
            "StrongPass123!"));
        var first = await register.Content.ReadFromJsonAsync<AuthResponse>();

        var rotated = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(first!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = await rotated.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotEqual(first.RefreshToken, second!.RefreshToken);

        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task RefreshTokenReplayRevokesActiveTokensForUser()
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            $"refresh-replay-{Guid.NewGuid():N}@aurum.test",
            "StrongPass123!"));
        var first = await register.Content.ReadFromJsonAsync<AuthResponse>();

        var rotated = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(first!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = await rotated.Content.ReadFromJsonAsync<AuthResponse>();

        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var secondRefreshAfterReplay = await _client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest(second!.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, secondRefreshAfterReplay.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesRefreshToken()
    {
        var register = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
            $"logout-{Guid.NewGuid():N}@aurum.test",
            "StrongPass123!"));
        var payload = await register.Content.ReadFromJsonAsync<AuthResponse>();

        var logout = await _client.PostAsJsonAsync("/api/auth/logout", new RefreshTokenRequest(payload!.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(payload.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefreshReplayAllowsOnlyOneRotation()
    {
        using var factory = new AuthApiFactory();
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        try
        {
            var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
                $"race-{Guid.NewGuid():N}@aurum.test",
                "StrongPass123!"));
            var payload = await register.Content.ReadFromJsonAsync<AuthResponse>();

            var attempts = await Task.WhenAll(
                client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(payload!.RefreshToken)),
                client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(payload.RefreshToken)));

            Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, attempts.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task RateLimitIsPartitionedByEndpointAndIp()
    {
        using var factory = new AuthApiFactory(authRateLimitPermitLimit: 2);
        await factory.InitializeAsync();
        var client = factory.CreateClient();
        try
        {
            var firstLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(
                $"missing-{Guid.NewGuid():N}@aurum.test",
                "StrongPass123!"));
            var secondLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(
                $"missing-{Guid.NewGuid():N}@aurum.test",
                "StrongPass123!"));
            var thirdLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(
                $"missing-{Guid.NewGuid():N}@aurum.test",
                "StrongPass123!"));
            var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(
                $"rate-{Guid.NewGuid():N}@aurum.test",
                "StrongPass123!"));

            Assert.Equal(HttpStatusCode.Unauthorized, firstLogin.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, secondLogin.StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, thirdLogin.StatusCode);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
