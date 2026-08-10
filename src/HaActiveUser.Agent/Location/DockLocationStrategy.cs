using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Location;

[SupportedOSPlatform("windows")]
public sealed class DockLocationStrategy : ILocationStrategy
{
    private readonly IReadOnlyList<string> _expectedDeviceIds;
    private readonly string _room;
    private readonly ILogger<DockLocationStrategy> _logger;

    public DockLocationStrategy(
        IReadOnlyList<string> expectedDeviceIds, string room, ILogger<DockLocationStrategy> logger)
    {
        _expectedDeviceIds = expectedDeviceIds;
        _room = room;
        _logger = logger;
    }

    public string Name => "dock";

    public LocationProbe Probe()
    {
        if (_expectedDeviceIds.Count == 0)
        {
            return LocationProbe.Indeterminate("not configured");
        }

        IReadOnlyList<PresentDevice> present;
        try
        {
            present = DeviceInstanceScanner.Enumerate();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PnP enumeration failed");
            return LocationProbe.Indeterminate("enumeration failed");
        }

        if (present.Count == 0)
        {
            return LocationProbe.Indeterminate("enumeration empty");
        }

        // Prefix matching so a config entry can name a whole dock rather than one child device.
        var matched = _expectedDeviceIds
            .Where(expected => !string.IsNullOrWhiteSpace(expected))
            .FirstOrDefault(expected => present.Any(d =>
                d.InstanceId.StartsWith(expected.Trim(), StringComparison.OrdinalIgnoreCase)));

        return matched is not null
            ? new LocationProbe(true, $"matched {matched}", _room)
            : new LocationProbe(false, "no configured dock device present");
    }
}
