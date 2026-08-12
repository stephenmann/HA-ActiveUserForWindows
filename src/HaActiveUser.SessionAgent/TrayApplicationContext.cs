using System.Diagnostics;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Sessions.UserInput;
using InputReporter = HaActiveUser.Agent.Sessions.UserInput.SessionAgent;

namespace HaActiveUser.SessionAgent;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private SessionAgentStatus? _displayedStatus;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open log", SystemIcons.Application.ToBitmap(), (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", SystemIcons.Error.ToBitmap(), (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Warning,
            Text = "HA Active User - Starting",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenLog();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _ = Task.Run(() => InputReporter.RunAsync(_stopping.Token));
    }

    private void RefreshStatus()
    {
        var status = InputReporter.Status;
        if (status == _displayedStatus)
        {
            return;
        }

        _displayedStatus = status;
        _trayIcon.Icon = status.IsConnected ? SystemIcons.Information : SystemIcons.Warning;
        _trayIcon.Text = status.IsConnected
            ? "HA Active User - Connected"
            : "HA Active User - Waiting for service";
    }

    private static void OpenLog()
    {
        var path = ConfigPaths.SessionAgentLogFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var target = File.Exists(path) ? path : Path.GetDirectoryName(path)!;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _statusTimer.Stop();
        _stopping.Cancel();
        _trayIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }
}