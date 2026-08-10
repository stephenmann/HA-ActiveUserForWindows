using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
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
            catch (Exception)
            {
                // The service may be stopped, restarting, or upgrading; keep trying quietly.
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
