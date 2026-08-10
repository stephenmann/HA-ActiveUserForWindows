using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaActiveUser.Agent.Hosting;

/// <summary>
/// <see cref="WindowsServiceLifetime"/> does not subscribe to session or power notifications by
/// default, so the agent would only ever learn about lock, unlock and resume by polling. Enabling
/// the flags in the constructor is the only supported way to opt in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly SystemEventBus _events;
    private readonly ILogger<SessionAwareServiceLifetime> _logger;

    public SessionAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        SystemEventBus events)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor)
    {
        _events = events;
        _logger = loggerFactory.CreateLogger<SessionAwareServiceLifetime>();

        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        var kind = changeDescription.Reason switch
        {
            SessionChangeReason.SessionLogon => SystemEventKind.SessionLogon,
            SessionChangeReason.SessionLogoff => SystemEventKind.SessionLogoff,
            SessionChangeReason.SessionLock => SystemEventKind.SessionLock,
            SessionChangeReason.SessionUnlock => SystemEventKind.SessionUnlock,
            SessionChangeReason.ConsoleConnect => SystemEventKind.SessionConnect,
            SessionChangeReason.ConsoleDisconnect => SystemEventKind.SessionDisconnect,
            SessionChangeReason.RemoteConnect => SystemEventKind.RemoteConnect,
            SessionChangeReason.RemoteDisconnect => SystemEventKind.RemoteDisconnect,
            _ => (SystemEventKind?)null
        };

        if (kind is null)
        {
            return;
        }

        _logger.LogDebug(
            "Session {SessionId} raised {Reason}", changeDescription.SessionId, changeDescription.Reason);
        _events.Publish(new SystemEvent(kind.Value, changeDescription.SessionId));
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                _events.Publish(new SystemEvent(SystemEventKind.Suspending));
                break;

            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeCritical:
                _events.Publish(new SystemEvent(SystemEventKind.Resumed));
                break;
        }

        return base.OnPowerEvent(powerStatus);
    }
}
