using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HaActiveUser.Agent.Sessions;
using HaActiveUser.Agent.Sessions.UserInput;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaActiveUser.Agent.Tests;

public class UserInputTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReportedIdleBecomesALastInputTime()
    {
        var clock = new FakeClock(T0);
        var tracker = new UserInputTracker(clock);

        tracker.Report("S-1-5-21-1", TimeSpan.FromSeconds(30));

        Assert.Equal(T0.AddSeconds(-30), tracker.LastInputFor("S-1-5-21-1"));
    }

    [Fact]
    public void UnknownUsersHaveNoInput() =>
        Assert.Null(new UserInputTracker(new FakeClock(T0)).LastInputFor("S-1-5-21-nobody"));

    [Fact]
    public void ReportsGoStaleSoADeadHelperDoesNotLookIdleForever()
    {
        var clock = new FakeClock(T0);
        var tracker = new UserInputTracker(clock);
        tracker.Report("S-1-5-21-1", TimeSpan.Zero);

        clock.Advance(UserInputProtocol.StaleAfter - TimeSpan.FromSeconds(1));
        Assert.NotNull(tracker.LastInputFor("S-1-5-21-1"));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(tracker.LastInputFor("S-1-5-21-1"));
    }
}

public class ReportedInputSessionProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReportedInputReplacesTheValueFromWindows()
    {
        var clock = new FakeClock(T0);
        var tracker = new UserInputTracker(clock);
        tracker.Report("S-1-5-21-1", TimeSpan.FromSeconds(5));

        var session = Session(lastInput: T0.AddDays(-5));
        var provider = Provider(tracker, session);

        Assert.Equal(T0.AddSeconds(-5), Assert.Single(provider.GetSessions()).LastInputUtc);
    }

    [Fact]
    public void LocalSessionsWithoutAReportHaveNoInputRatherThanStaleInput()
    {
        // Windows freezes LastInputTime at logon for console sessions, which would read as "active
        // five days ago" forever and could never be distinguished from a real idle period.
        var provider = Provider(new UserInputTracker(new FakeClock(T0)), Session(lastInput: T0.AddDays(-5)));

        Assert.Null(Assert.Single(provider.GetSessions()).LastInputUtc);
    }

    [Fact]
    public void RemoteSessionsKeepTheValueFromWindows()
    {
        var lastInput = T0.AddMinutes(-1);
        var provider = Provider(
            new UserInputTracker(new FakeClock(T0)), Session(lastInput: lastInput, isRemote: true));

        Assert.Equal(lastInput, Assert.Single(provider.GetSessions()).LastInputUtc);
    }

    private static ReportedInputSessionProvider Provider(IUserInputTracker tracker, params SessionSnapshot[] sessions) =>
        new(new StubSessionProvider(sessions), tracker, NullLogger<ReportedInputSessionProvider>.Instance);

    private static SessionSnapshot Session(DateTimeOffset lastInput, bool isRemote = false) => new()
    {
        SessionId = 1,
        Sid = "S-1-5-21-1",
        Domain = "MACHINE",
        UserName = "stephen",
        ConnectState = WtsConnectState.Active,
        IsLocked = false,
        LastInputUtc = lastInput,
        IsRemote = isRemote
    };

    private sealed class StubSessionProvider(IReadOnlyList<SessionSnapshot> sessions) : ISessionProvider
    {
        public IReadOnlyList<SessionSnapshot> GetSessions() => sessions;
    }
}

public class UserInputPipeServerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AReportedIdleTimeReachesTheTrackerAgainstTheCallersSid()
    {
        // A private pipe name keeps this off the pipe the installed service may already own.
        var pipeName = "HAActiveUser.Test." + Guid.NewGuid().ToString("n");
        var clock = new FakeClock(T0);
        var tracker = new UserInputTracker(clock);
        var server = new UserInputPipeServer(
            tracker, NullLogger<UserInputPipeServer>.Instance, pipeName);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StartAsync(cts.Token);

        try
        {
            // Identification level, as the real helper connects: enough to read the SID, not to impersonate.
            using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await client.ConnectAsync(10_000, cts.Token);

            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync("45000");

            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var lastInput = await WaitForInputAsync(tracker, sid, cts.Token);

            Assert.Equal(T0.AddSeconds(-45), lastInput);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<DateTimeOffset?> WaitForInputAsync(
        IUserInputTracker tracker, string sid, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (tracker.LastInputFor(sid) is { } lastInput)
            {
                return lastInput;
            }

            await Task.Delay(25, cancellationToken);
        }

        return null;
    }
}

public class UserInputPipeSecurityTests
{
    /// <summary>
    /// The round-trip test above cannot catch a too-narrow DACL: it connects as the pipe's own
    /// creator, and an owner is granted READ_CONTROL whatever the DACL says. A non-elevated logon
    /// helper is not the owner, so the granted rights are asserted directly.
    /// </summary>
    [Fact]
    public void AuthenticatedUsersMayOpenTheIdleReportPipeForWriting()
    {
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var granted = UserInputPipeServer.BuildSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(authenticatedUsers)
                && rule.AccessControlType == AccessControlType.Allow)
            .Aggregate(default(PipeAccessRights), (rights, rule) => rights | rule.PipeAccessRights);

        // GENERIC_WRITE maps to FILE_GENERIC_WRITE, and the access check needs every mapped bit.
        const PipeAccessRights RequiredForGenericWrite =
            PipeAccessRights.Write | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;

        Assert.Equal(RequiredForGenericWrite, granted & RequiredForGenericWrite);
    }

    [Fact]
    public void AuthenticatedUsersMayNotReadOtherUsersReports()
    {
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        var granted = UserInputPipeServer.BuildSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(authenticatedUsers)
                && rule.AccessControlType == AccessControlType.Allow)
            .Aggregate(default(PipeAccessRights), (rights, rule) => rights | rule.PipeAccessRights);

        Assert.Equal(default, granted & PipeAccessRights.ReadData);
        Assert.Equal(default, granted & PipeAccessRights.ChangePermissions);
        Assert.Equal(default, granted & PipeAccessRights.TakeOwnership);
    }
}
