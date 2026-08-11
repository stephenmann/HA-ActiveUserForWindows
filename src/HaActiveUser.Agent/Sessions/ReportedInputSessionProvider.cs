using HaActiveUser.Agent.Sessions.UserInput;
using Microsoft.Extensions.Logging;

namespace HaActiveUser.Agent.Sessions;

/// <summary>
/// Replaces the last-input time from the Terminal Services API, which Windows only maintains for
/// remote sessions, with what the per-session helper reports for local ones.
/// </summary>
public sealed class ReportedInputSessionProvider(
    ISessionProvider inner,
    IUserInputTracker tracker,
    ILogger<ReportedInputSessionProvider> logger) : ISessionProvider
{
    private readonly HashSet<string> _warnedAbout = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SessionSnapshot> GetSessions()
    {
        var sessions = inner.GetSessions();
        var merged = new List<SessionSnapshot>(sessions.Count);

        foreach (var session in sessions)
        {
            merged.Add(WithReportedInput(session));
        }

        return merged;
    }

    private SessionSnapshot WithReportedInput(SessionSnapshot session)
    {
        if (session.Sid is not { } sid)
        {
            return session;
        }

        if (tracker.LastInputFor(sid) is { } reported)
        {
            // The warning below is logged once per SID, so without this the log looks identical
            // whether reports started flowing or never arrived again.
            if (_warnedAbout.Remove(sid))
            {
                logger.LogInformation("Receiving idle reports from {Account}", session.Account);
            }

            return session with { LastInputUtc = reported };
        }

        if (!session.IsRemote && session.IsAttached && _warnedAbout.Add(sid))
        {
            logger.LogWarning(
                "No idle reports from {Account}; the session helper is not running, so activity cannot be detected",
                session.Account);
        }

        // Windows leaves this frozen at logon for local sessions, so stale data is worse than none.
        return session.IsRemote ? session : session with { LastInputUtc = null };
    }
}
