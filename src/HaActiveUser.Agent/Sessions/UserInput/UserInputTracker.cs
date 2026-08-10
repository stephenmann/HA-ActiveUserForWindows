using System.Collections.Concurrent;
using HaActiveUser.Agent.Abstractions;

namespace HaActiveUser.Agent.Sessions.UserInput;

public interface IUserInputTracker
{
    void Report(string sid, TimeSpan idle);

    /// <summary>The last input time for a signed-in user, or null when nothing recent has been reported.</summary>
    DateTimeOffset? LastInputFor(string sid);
}

public sealed class UserInputTracker(IClock clock) : IUserInputTracker
{
    private readonly ConcurrentDictionary<string, InputReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    public void Report(string sid, TimeSpan idle)
    {
        var now = clock.UtcNow;
        _reports[sid] = new InputReport(now - idle, now);
    }

    public DateTimeOffset? LastInputFor(string sid)
    {
        if (!_reports.TryGetValue(sid, out var report))
        {
            return null;
        }

        return clock.UtcNow - report.ReceivedUtc > UserInputProtocol.StaleAfter
            ? null
            : report.LastInputUtc;
    }

    private readonly record struct InputReport(DateTimeOffset LastInputUtc, DateTimeOffset ReceivedUtc);
}
