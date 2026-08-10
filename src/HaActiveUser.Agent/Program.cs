using HaActiveUser.Agent.Abstractions;
using HaActiveUser.Agent.Cli;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.DeviceProfiles;
using HaActiveUser.Agent.Hosting;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Mqtt;
using HaActiveUser.Agent.Presence;
using HaActiveUser.Agent.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This agent only runs on Windows.");
    return 1;
}

if (CommandLine.TryRun(args) is { } exitCode)
{
    return exitCode;
}

// Only the service path needs the data directory; the read-only CLI verbs must not create or
// re-ACL it, since they normally run unelevated.
ConfigDirectoryInitializer.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(
        ConfigPaths.LogFile,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Configuration
        .AddJsonFile(ConfigPaths.ConfigFile, optional: false, reloadOnChange: true)
        .AddEnvironmentVariables("HAAU_");

    builder.Services.AddSerilog();
    builder.Services.AddWindowsService(options => options.ServiceName = "HAActiveUser");

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
    builder.Services.AddSingleton<SystemEventBus>();
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
    builder.Services.AddSingleton<ISessionProvider, WtsSessionProvider>();

    builder.Services.AddSingleton<IDeviceProfileDetector>(sp => new DeviceProfileDetector(
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.DeviceProfile,
        sp.GetRequiredService<ILogger<DeviceProfileDetector>>()));

    builder.Services.AddSingleton(sp => DeviceIdentityFactory.Create(
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.DeviceName));

    builder.Services.AddSingleton<IPersonResolver>(sp =>
        new PersonResolver(sp.GetRequiredService<IOptions<AgentOptions>>().Value.Accounts));

    builder.Services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new MqttTopics(options.TopicPrefix, options.DiscoveryPrefix, sp.GetRequiredService<DeviceIdentity>());
    });

    builder.Services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new DiscoveryPayloadBuilder(
            sp.GetRequiredService<MqttTopics>(), sp.GetRequiredService<DeviceIdentity>(), options.Room);
    });

    builder.Services.AddSingleton<IHomeLocationDetector>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var home = options.HomeLocation;
        var loggers = sp.GetRequiredService<ILoggerFactory>();
        var strategies = new List<ILocationStrategy>();

        if (home.Wifi.Bssids.Count > 0 || home.Wifi.Ssids.Count > 0)
        {
            strategies.Add(new WifiLocationStrategy(
                home.Wifi, options.Room, loggers.CreateLogger<WifiLocationStrategy>()));
        }

        if (home.GatewayMacs.Count > 0)
        {
            strategies.Add(new GatewayMacLocationStrategy(
                home.GatewayMacs, options.Room, loggers.CreateLogger<GatewayMacLocationStrategy>()));
        }

        if (home.DockDeviceIds.Count > 0)
        {
            strategies.Add(new DockLocationStrategy(
                home.DockDeviceIds, options.Room, loggers.CreateLogger<DockLocationStrategy>()));
        }

        return new CompositeHomeLocationDetector(
            strategies,
            home.MatchMode,
            options.Room,
            home.PublishRawIdentifiers,
            loggers.CreateLogger<CompositeHomeLocationDetector>());
    });

    builder.Services.AddSingleton(sp => new LocationStabilizer(
        sp.GetRequiredService<IHomeLocationDetector>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.HomeLocation));

    builder.Services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new OccupancyEvaluator(
            sp.GetRequiredService<IPersonResolver>(),
            options.Room,
            TimeSpan.FromSeconds(Math.Max(1, options.IdleThresholdSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, options.AwayGraceSeconds)),
            options.HomeLocation.RequireForOccupancy);
    });

    builder.Services.AddSingleton<IMqttPublisher>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var device = sp.GetRequiredService<DeviceIdentity>();
        return new MqttPublisher(
            options.Mqtt,
            sp.GetRequiredService<MqttTopics>(),
            string.IsNullOrWhiteSpace(options.Mqtt.ClientId) ? $"haau-{device.DeviceId}" : options.Mqtt.ClientId,
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<ILogger<MqttPublisher>>());
    });

    builder.Services.AddSingleton(sp =>
    {
        var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new StatePublisher(
            sp.GetRequiredService<IMqttPublisher>(),
            sp.GetRequiredService<MqttTopics>(),
            sp.GetRequiredService<DiscoveryPayloadBuilder>(),
            sp.GetRequiredService<IPersonResolver>(),
            sp.GetRequiredService<IClock>(),
            TimeSpan.FromSeconds(Math.Max(10, options.IdleHeartbeatSeconds)),
            sp.GetRequiredService<ILogger<StatePublisher>>());
    });

    builder.Services.AddHostedService<AgentWorker>();

    // AddWindowsService registers a lifetime that ignores session and power notifications, so it
    // has to be swapped for one that opts in.
    if (WindowsServiceHelpers.IsWindowsService())
    {
        builder.Services.Replace(
            ServiceDescriptor.Singleton<IHostLifetime, SessionAwareServiceLifetime>());
    }

    var host = builder.Build();

    ValidateConfiguration(host.Services);

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static void ValidateConfiguration(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    var options = services.GetRequiredService<IOptions<AgentOptions>>().Value;

    if (options.Accounts.Count == 0)
    {
        logger.LogWarning(
            "No accounts are configured, so no presence entities will be created. Run --list-accounts and edit {Config}",
            ConfigPaths.ConfigFile);
    }

    var profile = services.GetRequiredService<IDeviceProfileDetector>().Detect();
    var gateRequired = options.HomeLocation.RequireForOccupancy ?? profile == DeviceProfile.Laptop;

    if (gateRequired && !options.HomeLocation.HasAnyStrategyConfigured)
    {
        logger.LogWarning(
            "This device is treated as a {Profile} so occupancy requires a home-location match, but no Wi-Fi, gateway or dock identifiers are configured. Occupancy will never turn on. Configure HomeLocation, or set HomeLocation.RequireForOccupancy to false.",
            profile);
    }
}

public partial class Program;
