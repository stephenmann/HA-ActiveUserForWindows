using HaActiveUser.Agent.Configuration;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Location;

/// <summary>
/// Combines the configured strategies into a single reading. Deliberately returns a room name
/// rather than a boolean so that mapping different BSSIDs to different rooms later is a
/// configuration change instead of a redesign.
/// </summary>
public sealed class CompositeHomeLocationDetector : IHomeLocationDetector
{
    private readonly IReadOnlyList<ILocationStrategy> _strategies;
    private readonly LocationMatchMode _matchMode;
    private readonly string _fallbackRoom;
    private readonly bool _publishRawIdentifiers;
    private readonly ILogger<CompositeHomeLocationDetector> _logger;

    public CompositeHomeLocationDetector(
        IReadOnlyList<ILocationStrategy> strategies,
        LocationMatchMode matchMode,
        string fallbackRoom,
        bool publishRawIdentifiers,
        ILogger<CompositeHomeLocationDetector> logger)
    {
        _strategies = strategies;
        _matchMode = matchMode;
        _fallbackRoom = fallbackRoom;
        _publishRawIdentifiers = publishRawIdentifiers;
        _logger = logger;
    }

    public bool HasStrategies => _strategies.Count > 0;

    public LocationReading Read()
    {
        if (_strategies.Count == 0)
        {
            return new LocationReading(LocationState.Unknown, null, "unknown", null);
        }

        var probes = _strategies
            .Select(s => (Strategy: s, Probe: SafeProbe(s)))
            .ToList();

        var decided = probes.Where(p => p.Probe.Matched is not null).ToList();
        var detail = _publishRawIdentifiers
            ? string.Join("; ", probes.Select(p => $"{p.Strategy.Name}={p.Probe.Detail}"))
            : null;

        if (decided.Count == 0)
        {
            return new LocationReading(LocationState.Unknown, null, "unknown", detail);
        }

        var matched = _matchMode == LocationMatchMode.All
            ? decided.All(p => p.Probe.Matched == true)
            : decided.Any(p => p.Probe.Matched == true);

        if (!matched)
        {
            return new LocationReading(LocationState.Away, null, "away", detail);
        }

        var room = decided.FirstOrDefault(p => p.Probe.Matched == true).Probe.Room ?? _fallbackRoom;
        return new LocationReading(LocationState.AtHome, room, "home", detail);
    }

    private LocationProbe SafeProbe(ILocationStrategy strategy)
    {
        try
        {
            return strategy.Probe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Location strategy {Strategy} threw; treating as indeterminate", strategy.Name);
            return LocationProbe.Indeterminate("error");
        }
    }
}
