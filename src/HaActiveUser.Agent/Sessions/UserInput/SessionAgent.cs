using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using HaActiveUser.Agent.Configuration;
using static HaActiveUser.Agent.Sessions.Native.UserInputNativeMethods;

namespace HaActiveUser.Agent.Sessions.UserInput;

/// <summary>
/// Runs inside an interactive session and reports how long the user has been idle. This exists
/// because GetLastInputInfo is per-session and the service lives in session 0, while
/// WTSINFOEX.LastInputTime is only maintained for remote sessions.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SessionAgent
{
    private static string? _lastNote;

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        HideOwnConsoleWindow();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReportUntilDisconnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                // A restarting service and a permanently wrong pipe ACL look identical from here,
                // so the reason is recorded rather than swallowed.
                Note(Describe(ex));
            }

            try
            {
                await Task.Delay(UserInputProtocol.ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        return 0;
    }

    private static async Task ReportUntilDisconnectedAsync(CancellationToken cancellationToken)
    {
        // Identification level only: if something ever squats the pipe name, it can read our SID
        // but cannot impersonate this user to open resources on their behalf.
        using var client = new NamedPipeClientStream(
            ".",
            UserInputProtocol.PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        await client.ConnectAsync((int)TimeSpan.FromSeconds(10).TotalMilliseconds, cancellationToken)
            .ConfigureAwait(false);

        Note("Connected to the service; reporting idle time.");

        await using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            var idle = GetIdleTime();
            await writer.WriteLineAsync(
                    ((long)idle.TotalMilliseconds).ToString(CultureInfo.InvariantCulture).AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);

            await Task.Delay(UserInputProtocol.ReportInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException =>
            "Access to the idle-report pipe was denied. The service is refusing reports from this "
            + "account, which usually means it is running an older build than this helper.",
        TimeoutException => "Timed out connecting to the idle-report pipe; the service may be stopped.",
        _ => $"{ex.GetType().Name}: {ex.Message}"
    };

    /// <summary>
    /// Writes to a per-user file because this runs as the user, who cannot see the service's log,
    /// and with a hidden console has nowhere else to report. Repeats are dropped so a permanent
    /// failure does not grow the file every ten seconds.
    /// </summary>
    private static void Note(string message)
    {
        if (message == _lastNote)
        {
            return;
        }

        _lastNote = message;

        try
        {
            var path = ConfigPaths.SessionAgentLogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (new FileInfo(path) is { Exists: true, Length: > 64 * 1024 })
            {
                File.Delete(path);
            }

            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reporting idle time matters more than recording that we could not say so.
        }
    }

    /// <summary>
    /// Hides the console the Run key gives us at logon, but only when this process is the console's
    /// sole owner. Launched from an existing shell the window belongs to the user, not to us.
    /// </summary>
    private static void HideOwnConsoleWindow()
    {
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        var processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
        {
            ShowWindow(window, SwHide);
        }
    }

    private static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values wrap every ~49.7 days; unchecked subtraction stays correct across the wrap.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
