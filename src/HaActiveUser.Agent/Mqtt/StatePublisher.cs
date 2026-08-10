using System.Text.Json;
using System.Text.Json.Nodes;
using HaActiveUser.Agent.Abstractions;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Presence;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Mqtt;

/// <summary>
/// Publishes discovery and state. Values are cached and only republished when they change, except
/// the idle counter which gets a heartbeat so it does not look stale in Home Assistant.
/// </summary>
public sealed class StatePublisher
{
    private readonly IMqttPublisher _mqtt;
    private readonly MqttTopics _topics;
    private readonly DiscoveryPayloadBuilder _discovery;
    private readonly IPersonResolver _people;
    private readonly IClock _clock;
    private readonly TimeSpan _idleHeartbeat;
    private readonly ILogger<StatePublisher> _logger;

    private readonly Dictionary<string, string> _lastPublished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastPublishedAt = new(StringComparer.Ordinal);

    public StatePublisher(
        IMqttPublisher mqtt,
        MqttTopics topics,
        DiscoveryPayloadBuilder discovery,
        IPersonResolver people,
        IClock clock,
        TimeSpan idleHeartbeat,
        ILogger<StatePublisher> logger)
    {
        _mqtt = mqtt;
        _topics = topics;
        _discovery = discovery;
        _people = people;
        _clock = clock;
        _idleHeartbeat = idleHeartbeat;
        _logger = logger;
    }

    public async Task PublishDiscoveryAsync(CancellationToken cancellationToken)
    {
        var payload = _discovery.Build(_people.KnownPeople);
        await _mqtt.PublishAsync(_topics.Discovery, payload, retain: true, cancellationToken).ConfigureAwait(false);

        // Home Assistant discards states that arrive before discovery, so force a full state resend.
        _lastPublished.Clear();
        _lastPublishedAt.Clear();
        _logger.LogInformation("Published discovery for {Count} people", _people.KnownPeople.Count);
    }

    /// <summary>Removes the device and all of its entities from Home Assistant.</summary>
    public Task PublishRemovalAsync(CancellationToken cancellationToken) =>
        _mqtt.PublishAsync(_topics.Discovery, DiscoveryPayloadBuilder.BuildRemoval(), retain: true, cancellationToken);

    public async Task PublishAsync(
        IReadOnlyList<PresenceState> people,
        LocationReading location,
        CancellationToken cancellationToken)
    {
        var activeUser = people.FirstOrDefault(p => p.IsOccupied)?.DisplayName ?? "none";
        await PublishIfChangedAsync(_topics.ActiveUser, activeUser, cancellationToken).ConfigureAwait(false);
        await PublishIfChangedAsync(
            _topics.AtHome, Onoff(location.State == LocationState.AtHome), cancellationToken).ConfigureAwait(false);
        await PublishIfChangedAsync(_topics.NetworkLocation, location.Label, cancellationToken).ConfigureAwait(false);

        foreach (var person in people)
        {
            await PublishIfChangedAsync(
                _topics.Occupancy(person.PersonKey), Onoff(person.IsOccupied), cancellationToken).ConfigureAwait(false);
            await PublishIfChangedAsync(
                _topics.Room(person.PersonKey), person.Room, cancellationToken).ConfigureAwait(false);
            await PublishIfChangedAsync(
                _topics.Locked(person.PersonKey), Onoff(person.IsLocked), cancellationToken).ConfigureAwait(false);
            await PublishIfChangedAsync(
                _topics.Idle(person.PersonKey),
                person.IdleSeconds < 0 ? "0" : person.IdleSeconds.ToString(),
                cancellationToken,
                _idleHeartbeat).ConfigureAwait(false);
            await PublishIfChangedAsync(
                _topics.Attributes(person.PersonKey),
                BuildAttributes(person, location),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildAttributes(PresenceState person, LocationReading location)
    {
        var attributes = new JsonObject
        {
            // Home Assistant templates use this to pick the most recent device when a person is
            // reported by more than one machine.
            ["last_active"] = person.LastActiveUtc?.ToUniversalTime().ToString("O"),
            ["signed_in"] = person.IsSignedIn,
            ["locked"] = person.IsLocked,
            ["idle_seconds"] = person.IdleSeconds < 0 ? null : person.IdleSeconds,
            ["room"] = person.Room,
            ["source_account"] = person.SourceAccount,
            ["session_id"] = person.SessionId,
            ["network_location"] = location.Label
        };

        if (location.RawDetail is not null)
        {
            attributes["location_detail"] = location.RawDetail;
        }

        return attributes.ToJsonString(JsonSerializerOptions.Default);
    }

    private async Task PublishIfChangedAsync(
        string topic,
        string payload,
        CancellationToken cancellationToken,
        TimeSpan? heartbeat = null)
    {
        var now = _clock.UtcNow;
        var unchanged = _lastPublished.TryGetValue(topic, out var previous)
            && string.Equals(previous, payload, StringComparison.Ordinal);

        if (unchanged && heartbeat is null)
        {
            return;
        }

        if (unchanged
            && _lastPublishedAt.TryGetValue(topic, out var publishedAt)
            && now - publishedAt < heartbeat!.Value)
        {
            return;
        }

        await _mqtt.PublishAsync(topic, payload, retain: true, cancellationToken).ConfigureAwait(false);
        _lastPublished[topic] = payload;
        _lastPublishedAt[topic] = now;
    }

    private static string Onoff(bool value) => value ? "ON" : "OFF";
}
