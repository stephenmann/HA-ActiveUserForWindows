using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using HaActiveUser.Agent.Sessions.Native;
using Microsoft.Extensions.Logging;
using static HaActiveUser.Agent.Sessions.Native.WtsNativeMethods;

namespace HaActiveUser.Agent.Sessions;

/// <summary>
/// Reads session state through the Terminal Services API. The agent runs in session 0, where
/// GetLastInputInfo only ever reports session 0's own input, so WTSINFOEX.LastInputTime is the
/// only idle source available to a service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WtsSessionProvider : ISessionProvider
{
    private readonly ILogger<WtsSessionProvider> _logger;
    private readonly Dictionary<string, string?> _sidCache = new(StringComparer.OrdinalIgnoreCase);

    // Windows 7 / Server 2008 R2 report WTS_SESSIONSTATE_LOCK and _UNLOCK the wrong way round.
    private static readonly bool InvertedLockFlags = Environment.OSVersion.Version < new Version(6, 2);

    public WtsSessionProvider(ILogger<WtsSessionProvider> logger) => _logger = logger;

    public IReadOnlyList<SessionSnapshot> GetSessions()
    {
        var sessions = new List<SessionSnapshot>();
        var buffer = IntPtr.Zero;

        try
        {
            if (WTSEnumerateSessionsW(CurrentServerHandle, 0, 1, out buffer, out var count) == 0)
            {
                _logger.LogWarning(
                    "WTSEnumerateSessions failed with error {Error}", Marshal.GetLastWin32Error());
                return sessions;
            }

            var entrySize = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * entrySize));
                var snapshot = ReadSession(entry.SessionId);
                if (snapshot is not null)
                {
                    sessions.Add(snapshot);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }

        return sessions;
    }

    private SessionSnapshot? ReadSession(int sessionId)
    {
        var info = QueryInfoEx(sessionId);
        if (info is null)
        {
            return null;
        }

        var level1 = info.Value;
        if (string.IsNullOrEmpty(level1.UserName))
        {
            return null;
        }

        var connectState = Enum.IsDefined(typeof(WtsConnectState), level1.SessionState)
            ? (WtsConnectState)level1.SessionState
            : WtsConnectState.Down;

        return new SessionSnapshot
        {
            SessionId = sessionId,
            Domain = level1.DomainName,
            UserName = level1.UserName,
            Sid = ResolveSid(level1.DomainName, level1.UserName),
            ConnectState = connectState,
            IsLocked = IsLocked(level1.SessionFlags),
            LastInputUtc = ToDateTimeOffset(level1.LastInputTime),
            IsRemote = QueryIsRemote(sessionId)
        };
    }

    private WtsInfoExLevel1? QueryInfoEx(int sessionId)
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformationW(
                    CurrentServerHandle, sessionId, WtsInfoClass.WtsSessionInfoEx, out buffer, out var bytes) == 0
                || bytes < InfoExLevel1Offset)
            {
                return null;
            }

            var level = Marshal.ReadInt32(buffer);
            if (level != 1)
            {
                return null;
            }

            return Marshal.PtrToStructure<WtsInfoExLevel1>(buffer + InfoExLevel1Offset);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read extended info for session {SessionId}", sessionId);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private bool QueryIsRemote(int sessionId)
    {
        var buffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformationW(
                    CurrentServerHandle, sessionId, WtsInfoClass.WtsIsRemoteSession, out buffer, out var bytes) == 0
                || bytes < 1)
            {
                return false;
            }

            return Marshal.ReadByte(buffer) != 0;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    private static bool IsLocked(int sessionFlags) => sessionFlags switch
    {
        WtsSessionStateLock => !InvertedLockFlags,
        WtsSessionStateUnlock => InvertedLockFlags,
        _ => false
    };

    private static DateTimeOffset? ToDateTimeOffset(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private string? ResolveSid(string? domain, string? userName)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return null;
        }

        var account = string.IsNullOrEmpty(domain) ? userName : $"{domain}\\{userName}";
        if (_sidCache.TryGetValue(account, out var cached))
        {
            return cached;
        }

        string? sid = null;
        try
        {
            sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            _logger.LogDebug(ex, "Could not translate {Account} to a SID", account);
        }

        _sidCache[account] = sid;
        return sid;
    }
}
