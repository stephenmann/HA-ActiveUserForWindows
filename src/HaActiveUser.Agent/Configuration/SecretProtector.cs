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

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException ex)
        {
            // Not a blob at all, so almost always a plaintext password typed into the config file.
            throw new ConfigurationException(
                "Mqtt.ProtectedPassword is not a protected secret; it looks like a plaintext value. "
                + "Do not edit that field by hand - run \"HaActiveUser.Agent.exe --set-password\" from an elevated prompt.",
                ex);
        }

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException ex)
        {
            throw new ConfigurationException(
                "Mqtt.ProtectedPassword could not be decrypted. Secrets are bound to the machine that created them, "
                + "so a config file copied from another machine will not work - re-run --set-password here.",
                ex);
        }
    }
}
