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
    public async Task LoginRejectsWrongPassword()
    {
        var email = $"login-{Guid.NewGuid():N}@aurum.test";
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "StrongPass123!"));

        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPass123!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
}
