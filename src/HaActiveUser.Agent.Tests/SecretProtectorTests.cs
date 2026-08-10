using HaActiveUser.Agent.Configuration;
using Xunit;

namespace HaActiveUser.Agent.Tests;

public class SecretProtectorTests
{
    private static readonly DpapiSecretProtector Protector = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSecretsResolveToNull(string? value) => Assert.Null(Protector.Unprotect(value));

    [Fact]
    public void RoundTripsThroughDpapi()
    {
        var secret = Protector.Protect("correct horse battery staple");

        Assert.Equal("correct horse battery staple", Protector.Unprotect(secret));
    }

    [Fact]
    public void PlaintextInTheConfigFileIsCalledOutAsSuch()
    {
        var ex = Assert.Throws<ConfigurationException>(() => Protector.Unprotect("hunter2!password"));

        Assert.Contains("plaintext", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--set-password", ex.Message);
    }

    [Fact]
    public void ABlobFromAnotherMachineReportsTheMachineBinding()
    {
        // Well-formed base64 that is not a DPAPI blob, which is what a copied config looks like.
        var ex = Assert.Throws<ConfigurationException>(
            () => Protector.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));

        Assert.Contains("machine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
