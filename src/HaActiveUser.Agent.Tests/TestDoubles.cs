using HaActiveUser.Agent.Abstractions;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Presence;
using HaActiveUser.Agent.Sessions;

namespace HaActiveUser.Agent.Tests;

internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakeLocationDetector : IHomeLocationDetector
{
    public LocationReading Next { get; set; } = new(LocationState.AtHome, "Office", "home", null);

    public LocationReading Read() => Next;
}

internal static class Build
{
    public static readonly DateTimeOffset T0 = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public static SessionSnapshot Session(
        int sessionId = 1,
        string user = "stephen",
        string domain = "DESKTOP",
        string? sid = "S-1-5-21-1-1-1-1001",
        WtsConnectState state = WtsConnectState.Active,
        bool locked = false,
        DateTimeOffset? lastInput = null) =>
        new()
        {
            SessionId = sessionId,
            UserName = user,
            Domain = domain,
            Sid = sid,
            ConnectState = state,
            IsLocked = locked,
            LastInputUtc = lastInput ?? T0
        };

    public static PersonResolver Resolver(params AccountMapping[] mappings) =>
        new(mappings.Length > 0
            ? mappings
            : [new AccountMapping { Account = "DESKTOP\\stephen", PersonKey = "stephen", DisplayName = "Stephen" }]);

    public static OccupancyEvaluator Evaluator(
        IPersonResolver? resolver = null,
        int idleThresholdSeconds = 600,
        int awayGraceSeconds = 60,
        bool? requireGate = null) =>
        new(resolver ?? Resolver(),
            "Office",
            TimeSpan.FromSeconds(idleThresholdSeconds),
            TimeSpan.FromSeconds(awayGraceSeconds),
            requireGate);

    public static LocationReading AtHome(string room = "Office") => new(LocationState.AtHome, room, "home", null);

    public static LocationReading Away() => new(LocationState.Away, null, "away", null);

    public static LocationReading UnknownLocation() => new(LocationState.Unknown, null, "unknown", null);
}
