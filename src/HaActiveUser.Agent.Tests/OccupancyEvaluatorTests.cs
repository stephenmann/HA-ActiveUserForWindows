using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.DeviceProfiles;
using HaActiveUser.Agent.Presence;
using HaActiveUser.Agent.Sessions;
using Xunit;

namespace HaActiveUser.Agent.Tests;

public class OccupancyEvaluatorTests
{
    private static EvaluationInput Input(
        IEnumerable<SessionSnapshot>? sessions = null,
        Location.LocationReading? location = null,
        DeviceProfile profile = DeviceProfile.Desktop,
        DateTimeOffset? now = null) =>
        new(sessions?.ToList() ?? [],
            location ?? Build.AtHome(),
            profile,
            now ?? Build.T0);

    [Fact]
    public void ActiveUnlockedSessionIsOccupied()
    {
        var result = Build.Evaluator().Evaluate(Input([Build.Session()]));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal("Office", person.Room);
        Assert.Equal("stephen", person.PersonKey);
        Assert.Equal("Stephen", person.DisplayName);
    }

    [Fact]
    public void ConfiguredPeopleAlwaysGetAState()
    {
        var result = Build.Evaluator().Evaluate(Input());

        var person = Assert.Single(result);
        Assert.False(person.IsOccupied);
        Assert.False(person.IsSignedIn);
    }

    [Fact]
    public void UntrackedAccountsAreIgnored()
    {
        var result = Build.Evaluator().Evaluate(Input([Build.Session(user: "someoneelse", sid: "S-1-5-21-9")]));

        Assert.False(Assert.Single(result).IsOccupied);
    }

    [Fact]
    public void LogoffClearsOccupancyAfterGrace()
    {
        var evaluator = Build.Evaluator(awayGraceSeconds: 60);
        evaluator.Evaluate(Input([Build.Session()]));

        var duringGrace = evaluator.Evaluate(Input(now: Build.T0.AddSeconds(30)));
        Assert.True(Assert.Single(duringGrace).IsOccupied);

        var afterGrace = evaluator.Evaluate(Input(now: Build.T0.AddSeconds(61)));
        Assert.False(Assert.Single(afterGrace).IsOccupied);
    }

    [Fact]
    public void LockedSessionStopsCountingAfterGrace()
    {
        var evaluator = Build.Evaluator(awayGraceSeconds: 60);
        evaluator.Evaluate(Input([Build.Session()]));

        var locked = Build.Session(locked: true);
        var result = evaluator.Evaluate(Input([locked], now: Build.T0.AddSeconds(61)));

        var person = Assert.Single(result);
        Assert.False(person.IsOccupied);
        Assert.True(person.IsLocked);
        Assert.True(person.IsSignedIn);
    }

    [Fact]
    public void DisconnectedRdpSessionIsNotOccupancy()
    {
        var session = Build.Session(state: WtsConnectState.Disconnected);

        var result = Build.Evaluator(awayGraceSeconds: 0).Evaluate(Input([session]));

        Assert.False(Assert.Single(result).IsOccupied);
    }

    [Fact]
    public void CrossingTheIdleThresholdClearsOccupancy()
    {
        var evaluator = Build.Evaluator(idleThresholdSeconds: 600, awayGraceSeconds: 0);
        var session = Build.Session(lastInput: Build.T0);

        Assert.True(Assert.Single(evaluator.Evaluate(Input([session], now: Build.T0.AddSeconds(599)))).IsOccupied);
        Assert.False(Assert.Single(evaluator.Evaluate(Input([session], now: Build.T0.AddSeconds(601)))).IsOccupied);
    }

    [Fact]
    public void IdleSecondsReflectsTheMostRecentInput()
    {
        var result = Build.Evaluator().Evaluate(
            Input([Build.Session(lastInput: Build.T0.AddSeconds(-45))], now: Build.T0));

        Assert.Equal(45, Assert.Single(result).IdleSeconds);
    }

    [Fact]
    public void FastUserSwitchingPrefersTheAttachedSession()
    {
        var resolver = Build.Resolver(
            new AccountMapping { Account = "DESKTOP\\stephen", PersonKey = "stephen", DisplayName = "Stephen" },
            new AccountMapping { Account = "DESKTOP\\guest", PersonKey = "guest", DisplayName = "Guest" });

        var sessions = new[]
        {
            Build.Session(sessionId: 1, user: "stephen", sid: "S-1-1", state: WtsConnectState.Disconnected),
            Build.Session(sessionId: 2, user: "guest", sid: "S-1-2")
        };

        var result = Build.Evaluator(resolver, awayGraceSeconds: 0).Evaluate(Input(sessions));

        Assert.False(result.Single(p => p.PersonKey == "stephen").IsOccupied);
        Assert.True(result.Single(p => p.PersonKey == "guest").IsOccupied);
    }

    [Fact]
    public void TwoAccountsMappedToOnePersonAggregate()
    {
        var resolver = Build.Resolver(
            new AccountMapping { Account = "DESKTOP\\stephen", PersonKey = "stephen", DisplayName = "Stephen" },
            new AccountMapping { Account = "CORP\\sflowers", PersonKey = "stephen" });

        var sessions = new[]
        {
            Build.Session(sessionId: 1, user: "stephen", sid: "S-1-1", state: WtsConnectState.Disconnected),
            Build.Session(sessionId: 2, user: "sflowers", domain: "CORP", sid: "S-1-2")
        };

        var result = Build.Evaluator(resolver, awayGraceSeconds: 0).Evaluate(Input(sessions));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal(2, person.SessionId);
    }

    [Fact]
    public void LaptopActiveButAwayIsStillOccupiedAndReportsAway()
    {
        var result = Build.Evaluator().Evaluate(
            Input([Build.Session()], Build.Away(), DeviceProfile.Laptop));

        // Occupancy follows input and lock state only; location decides which room it counts for.
        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal(RoomNames.Away, person.Room);
    }

    [Fact]
    public void DesktopAwayReportsTheConfiguredRoom()
    {
        var result = Build.Evaluator().Evaluate(
            Input([Build.Session()], Build.Away(), DeviceProfile.Desktop));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal("Office", person.Room);
    }

    [Fact]
    public void UnknownLocationOnALaptopReportsUnknownRoom()
    {
        var result = Build.Evaluator().Evaluate(
            Input([Build.Session()], Build.UnknownLocation(), DeviceProfile.Laptop));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal(RoomNames.Unknown, person.Room);
    }

    [Fact]
    public void DisablingTheGateReportsTheConfiguredRoomEvenWhenAway()
    {
        var result = Build.Evaluator(requireGate: false).Evaluate(
            Input([Build.Session()], Build.Away(), DeviceProfile.Laptop));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal("Office", person.Room);
    }

    [Fact]
    public void EnablingTheGateReportsAwayOnADesktop()
    {
        var result = Build.Evaluator(requireGate: true).Evaluate(
            Input([Build.Session()], Build.Away(), DeviceProfile.Desktop));

        var person = Assert.Single(result);
        Assert.True(person.IsOccupied);
        Assert.Equal(RoomNames.Away, person.Room);
    }

    [Fact]
    public void LastActiveIsStampedForTieBreakingAcrossDevices()
    {
        var evaluator = Build.Evaluator();
        var at = Build.T0.AddSeconds(10);

        var result = evaluator.Evaluate(Input([Build.Session(lastInput: at)], now: at));

        Assert.Equal(at, Assert.Single(result).LastActiveUtc);
    }

    [Fact]
    public void SessionsWithoutInputTimestampsDoNotCount()
    {
        var session = Build.Session() with { LastInputUtc = null };

        var result = Build.Evaluator(awayGraceSeconds: 0).Evaluate(Input([session]));

        var person = Assert.Single(result);
        Assert.False(person.IsOccupied);
        Assert.Equal(-1, person.IdleSeconds);
    }
}
