using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Mqtt;
using HaActiveUser.Agent.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HaActiveUser.Agent.Cli;

/// <summary>
/// Setup helpers. Everything here runs interactively and exits; the service path is never entered.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CommandLine
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Returns an exit code when a verb was handled, or null to continue starting the service.</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        return args[0].ToLowerInvariant() switch
        {
            "--set-password" => SetPassword(),
            "--list-accounts" => ListAccounts(),
            "--list-devices" => ListDevices(args.Skip(1).FirstOrDefault()),
            "--remove-from-ha" => RemoveFromHomeAssistant(),
            "--help" or "-h" or "/?" => ShowHelp(),
            _ => ShowHelp()
        };
    }

    private static int ShowHelp()
    {
        Console.WriteLine("""
            HA Active User for Windows

              --set-password    Encrypt the MQTT broker password into the config file.
              --list-accounts   Show the signed-in accounts and their SIDs, for the Accounts mapping.
              --list-devices    List present PnP devices, for HomeLocation.DockDeviceIds.
                                Pass a filter to narrow the list, e.g. --list-devices dock
              --remove-from-ha  Delete this machine's device and entities from Home Assistant.

            Run with no arguments to start the service.
            Config file: 
            """ + ConfigPaths.ConfigFile);

        return 0;
    }

    private static int SetPassword()
    {
        Console.Write("MQTT password: ");
        var password = ReadHidden();
        Console.WriteLine();

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("No password entered; nothing changed.");
            return 1;
        }

        ConfigDirectoryInitializer.EnsureCreated();

        try
        {
            var json = File.ReadAllText(ConfigPaths.ConfigFile);
            var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            if (root[AgentOptions.SectionName] is not JsonObject agent)
            {
                agent = new JsonObject();
                root[AgentOptions.SectionName] = agent;
            }

            if (agent["Mqtt"] is not JsonObject mqtt)
            {
                mqtt = new JsonObject();
                agent["Mqtt"] = mqtt;
            }

            mqtt["ProtectedPassword"] = new DpapiSecretProtector().Protect(password);
            File.WriteAllText(ConfigPaths.ConfigFile, root.ToJsonString(WriteOptions));
        }
        catch (UnauthorizedAccessException)
        {
            // The config directory is deliberately restricted to SYSTEM and Administrators.
            Console.Error.WriteLine($"Access denied writing {ConfigPaths.ConfigFile}.");
            Console.Error.WriteLine("Run this command from an elevated prompt.");
            return 1;
        }

        Console.WriteLine($"Password encrypted into {ConfigPaths.ConfigFile}.");
        Console.WriteLine("The secret is bound to this machine; re-run this on each machine.");
        return 0;
    }

    private static int ListAccounts()
    {
        var provider = new WtsSessionProvider(NullLogger<WtsSessionProvider>.Instance);
        var sessions = provider.GetSessions();

        if (sessions.Count == 0)
        {
            Console.WriteLine("No interactive sessions found. Sign in and try again.");
            return 0;
        }

        Console.WriteLine($"{"Session",-8} {"Account",-32} {"State",-14} {"Locked",-7} SID");
        foreach (var session in sessions)
        {
            Console.WriteLine(
                $"{session.SessionId,-8} {session.Account,-32} {session.ConnectState,-14} {session.IsLocked,-7} {session.Sid}");
        }

        Console.WriteLine();
        Console.WriteLine("Use the SID (most reliable) or the Account in the Accounts section of the config.");
        return 0;
    }

    private static int ListDevices(string? filter)
    {
        var devices = DeviceInstanceScanner.Enumerate()
            .Where(d => filter is null
                || d.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || d.InstanceId.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Description, StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            Console.WriteLine(device.Description);
            Console.WriteLine($"    {device.InstanceId}");
        }

        Console.WriteLine();
        Console.WriteLine("Copy an instance ID (or a stable prefix of it) into HomeLocation.DockDeviceIds.");
        return 0;
    }

    /// <summary>
    /// Publishing an empty payload to the retained discovery topic is how Home Assistant is told to
    /// delete the device. Without it, uninstalling leaves orphaned entities behind forever.
    /// </summary>
    private static int RemoveFromHomeAssistant()
    {
        try
        {
            var options = LoadOptions();
            var device = DeviceIdentityFactory.Create(options.DeviceName);
            var topics = new MqttTopics(options.TopicPrefix, options.DiscoveryPrefix, device);

            var publisher = new MqttPublisher(
                options.Mqtt,
                topics,
                $"haau-remove-{device.DeviceId}",
                new DpapiSecretProtector(),
                NullLogger<MqttPublisher>.Instance);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            RemoveAsync(publisher, topics, timeout.Token).GetAwaiter().GetResult();

            Console.WriteLine("Removed this device from Home Assistant.");
            return 0;
        }
        catch (Exception ex)
        {
            // Uninstall must never fail because the broker happened to be unreachable.
            Console.Error.WriteLine($"Could not remove the device from Home Assistant: {ex.Message}");
            Console.Error.WriteLine(
                "Delete it manually in Home Assistant, or clear the retained discovery topic on the broker.");
            return 0;
        }
    }

    private static async Task RemoveAsync(
        MqttPublisher publisher, MqttTopics topics, CancellationToken cancellationToken)
    {
        await using (publisher)
        {
            await publisher.StartAsync(cancellationToken);
            await publisher.PublishAsync(topics.Discovery, string.Empty, retain: true, cancellationToken);
            await publisher.PublishAsync(topics.Availability, string.Empty, retain: true, cancellationToken);
            await publisher.StopAsync(cancellationToken);
        }
    }

    private static AgentOptions LoadOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(ConfigPaths.ConfigFile, optional: true)
            .AddEnvironmentVariables("HAAU_")
            .Build();

        var options = new AgentOptions();
        configuration.GetSection(AgentOptions.SectionName).Bind(options);
        return options;
    }

    private static string ReadHidden()
    {
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
