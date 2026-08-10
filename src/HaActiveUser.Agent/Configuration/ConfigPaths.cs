using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaActiveUser.Agent.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace HaActiveUser.Agent.Configuration;

public static class ConfigPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HAActiveUser");

    public static string ConfigFile => Path.Combine(RootDirectory, "config.json");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string LogFile => Path.Combine(LogDirectory, "agent-.log");
}

[SupportedOSPlatform("windows")]
public static class ConfigDirectoryInitializer
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigPaths.RootDirectory);
        Directory.CreateDirectory(ConfigPaths.LogDirectory);

        // The seed file has to be written before the ACL is tightened, otherwise an unelevated
        // first run locks itself out of the directory it just created.
        if (!File.Exists(ConfigPaths.ConfigFile))
        {
            File.WriteAllText(ConfigPaths.ConfigFile, DefaultConfigJson());
        }

        Restrict(ConfigPaths.RootDirectory);
    }

    /// <summary>
    /// The config holds a DPAPI-protected broker password. Machine-scope DPAPI can be decrypted by
    /// anything running on the box, so the file itself must not be readable by ordinary users.
    /// </summary>
    private static void Restrict(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var sid in new[]
                     {
                         WellKnownSidType.LocalSystemSid,
                         WellKnownSidType.BuiltinAdministratorsSid
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PrivilegeNotHeldException or InvalidOperationException)
        {
            // Running unelevated for a CLI verb; the installer sets the ACL at install time.
        }
    }

    public static string DefaultConfigJson()
    {
        var document = new JsonObject
        {
            [AgentOptions.SectionName] = new JsonObject
            {
                ["Room"] = "Office",
                ["DeviceProfile"] = nameof(DeviceProfileSetting.Auto),
                ["IdleThresholdSeconds"] = 600,
                ["AwayGraceSeconds"] = 60,
                ["PollIntervalSeconds"] = 10,
                ["Accounts"] = DefaultAccounts(),
                ["HomeLocation"] = new JsonObject
                {
                    ["MatchMode"] = nameof(LocationMatchMode.Any),
                    ["Wifi"] = new JsonObject
                    {
                        ["Bssids"] = new JsonArray(),
                        ["Ssids"] = new JsonArray()
                    },
                    ["GatewayMacs"] = new JsonArray(),
                    ["DockDeviceIds"] = new JsonArray(),
                    ["AwayGraceSeconds"] = 120,
                    ["ResumeSettleSeconds"] = 30,
                    ["PublishRawIdentifiers"] = false
                },
                ["Mqtt"] = new JsonObject
                {
                    ["Host"] = "homeassistant.local",
                    ["Port"] = 1883,
                    ["Username"] = "",
                    ["ProtectedPassword"] = "",
                    ["Tls"] = new JsonObject
                    {
                        ["Enabled"] = false
                    }
                }
            }
        };

        return document.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Seeds the account mapping from the signed-in user. This runs as LocalSystem, where
    /// <see cref="Environment.UserName"/> is the machine account and would never match a session.
    /// </summary>
    private static JsonArray DefaultAccounts()
    {
        var accounts = new JsonArray();

        SessionSnapshot? session = null;
        try
        {
            session = new WtsSessionProvider(NullLogger<WtsSessionProvider>.Instance)
                .GetSessions()
                .Where(candidate => !string.IsNullOrEmpty(candidate.Sid))
                .OrderByDescending(candidate => candidate.IsAttached)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            // Best effort only; --list-accounts fills this in.
        }

        if (session is not null)
        {
            accounts.Add(new JsonObject
            {
                ["Sid"] = session.Sid,
                ["Account"] = session.Account,
                ["PersonKey"] = "person1",
                ["DisplayName"] = session.Account
            });
        }

        return accounts;
    }
}
