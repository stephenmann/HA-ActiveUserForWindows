using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HaActiveUser.Agent.Configuration;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string? Unprotect(string? protectedValue);
}

/// <summary>
/// DPAPI with machine scope. The service runs as LocalSystem, so a user scope would be unusable,
/// and the config file is ACLed to SYSTEM and Administrators to compensate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HAActiveUserForWindows/v1");

    public string Protect(string plaintext) =>
        Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine));

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "A protected secret could not be decrypted. Secrets are machine-bound; re-run --set-password on this machine.",
                ex);
        }
    }
}
