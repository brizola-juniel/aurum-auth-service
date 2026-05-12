using AuthService.Security;

namespace AuthService.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void VerifyAcceptsOriginalPasswordAndRejectsDifferentPassword()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash("StrongPass123!");

        Assert.True(hasher.Verify("StrongPass123!", hash));
        Assert.False(hasher.Verify("OtherPass123!", hash));
        Assert.DoesNotContain("StrongPass123!", hash);
    }
}
