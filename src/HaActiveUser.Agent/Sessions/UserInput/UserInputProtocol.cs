namespace HaActiveUser.Agent.Sessions.UserInput;

/// <summary>
/// Contract between the service and the per-session helper. The service runs in session 0, where
/// GetLastInputInfo only sees session 0, so each interactive session reports its own idle time.
/// </summary>
public static class UserInputProtocol
{
    public const string PipeName = "HAActiveUser.input";

    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);

    /// <summary>Reports older than this are discarded, so a dead helper reads as "no data" rather than "idle forever".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);
}
