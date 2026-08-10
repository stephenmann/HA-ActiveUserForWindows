using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HaActiveUser.Agent.Identity;

public sealed record DeviceIdentity(string DeviceId, string DeviceName)
{
    public string DiscoveryObjectId => DeviceId;

    public string DeviceIdentifier => $"haau_{DeviceId}";
}

[SupportedOSPlatform("windows")]
public static class DeviceIdentityFactory
{
    public static DeviceIdentity Create(string? deviceNameOverride)
    {
        var name = string.IsNullOrWhiteSpace(deviceNameOverride)
            ? Environment.MachineName
            : deviceNameOverride.Trim();

        return new DeviceIdentity(ReadMachineGuid() ?? Slug.Make(Environment.MachineName), name);
    }

    /// <summary>MachineGuid is stable across renames and network changes, unlike the hostname or a MAC.</summary>
    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            var value = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(value) ? null : Slug.Make(value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
