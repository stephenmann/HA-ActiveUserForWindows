using HaActiveUser.Agent.DeviceProfiles;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Sessions;

namespace HaActiveUser.Agent.Presence;

public sealed record EvaluationInput(
    IReadOnlyList<SessionSnapshot> Sessions,
    LocationReading Location,
    DeviceProfile Profile,
    DateTimeOffset Now);

/// <summary>
/// Turns raw session and location data into per-person occupancy. Pure logic: all time arrives
/// through <see cref="EvaluationInput.Now"/> so the debounce behaviour is unit-testable.
/// </summary>
public sealed class OccupancyEvaluator
{
    private readonly IPersonResolver _people;
    private readonly string _homeRoom;
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _awayGrace;
    private readonly bool? _requireLocationGate;

    private readonly Dictionary<string, DateTimeOffset> _lastRawActiveAt = new(StringComparer.OrdinalIgnoreCase);

    public OccupancyEvaluator(
        IPersonResolver people,
        string homeRoom,
        TimeSpan idleThreshold,
        TimeSpan awayGrace,
        bool? requireLocationGate)
    {
        _people = people;
        _homeRoom = homeRoom;
        _idleThreshold = idleThreshold;
        _awayGrace = awayGrace;
        _requireLocationGate = requireLocationGate;
    }

    public IReadOnlyList<PresenceState> Evaluate(EvaluationInput input)
    {
        var gateRequired = _requireLocationGate ?? input.Profile == DeviceProfile.Laptop;
        var locationOk = !gateRequired || input.Location.State == LocationState.AtHome;
        var locationUnknown = gateRequired && input.Location.State == LocationState.Unknown;

        var byPerson = new Dictionary<string, List<SessionSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in input.Sessions)
        {
            var personKey = _people.Resolve(session);
            if (personKey is null)
            {
                continue;
            }

            if (!byPerson.TryGetValue(personKey, out var list))
            {
                list = [];
                byPerson[personKey] = list;
            }

            list.Add(session);
        }

        var results = new List<PresenceState>(_people.KnownPeople.Count);

        // Emit a state for every configured person, even with no session, so entities exist in
        // Home Assistant when nobody is signed in instead of going unavailable.
        foreach (var person in _people.KnownPeople)
        {
            byPerson.TryGetValue(person.PersonKey, out var sessions);
            results.Add(Evaluate(person, sessions, input, locationOk, locationUnknown));
        }

        return results;
    }

    private PresenceState Evaluate(
        PersonDescriptor person,
        List<SessionSnapshot>? sessions,
        EvaluationInput input,
        bool locationOk,
        bool locationUnknown)
    {
        var now = input.Now;
        sessions ??= [];

        // Fast user switching and RDP mean one person can own several sessions; the best one wins.
        var best = sessions
            .OrderByDescending(s => IsUsable(s, now))
            .ThenByDescending(s => s.LastInputUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        var rawActive = sessions.Any(s => IsUsable(s, now));

        if (rawActive)
        {
            _lastRawActiveAt[person.PersonKey] = now;
        }

        var isActive = rawActive
            || (_lastRawActiveAt.TryGetValue(person.PersonKey, out var lastActive)
                && now - lastActive < _awayGrace);

        var room = locationUnknown
            ? RoomNames.Unknown
            : locationOk ? input.Location.Room ?? _homeRoom : RoomNames.Away;

        var lastInput = sessions
            .Select(s => s.LastInputUtc)
            .Where(t => t is not null)
            .Max();

        var idleSeconds = lastInput is null
            ? int.MaxValue
            : (int)Math.Max(0, Math.Round((now - lastInput.Value).TotalSeconds));

        return new PresenceState(
            PersonKey: person.PersonKey,
            DisplayName: person.DisplayName,
            IsOccupied: isActive && locationOk,
            IsSignedIn: sessions.Count > 0,
            IsLocked: best?.IsLocked ?? false,
            IdleSeconds: idleSeconds == int.MaxValue ? -1 : idleSeconds,
            Room: room,
            LastActiveUtc: rawActive
                ? now
                : _lastRawActiveAt.TryGetValue(person.PersonKey, out var seen) ? seen : null,
            SourceAccount: best?.Account,
            SessionId: best?.SessionId);
    }

    /// <summary>
    /// A session only counts when the user is attached to it, it is unlocked, and input has been
    /// seen recently. Disconnected RDP sessions keep running with no one in front of them.
    /// </summary>
    private bool IsUsable(SessionSnapshot session, DateTimeOffset now) =>
        session.IsAttached
        && !session.IsLocked
        && session.LastInputUtc is { } lastInput
        && now - lastInput < _idleThreshold;
}
