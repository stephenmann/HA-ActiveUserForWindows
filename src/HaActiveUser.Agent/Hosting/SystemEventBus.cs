using System.Threading.Channels;

namespace HaActiveUser.Agent.Hosting;

public enum SystemEventKind
{
    SessionLogon,
    SessionLogoff,
    SessionLock,
    SessionUnlock,
    SessionConnect,
    SessionDisconnect,
    RemoteConnect,
    RemoteDisconnect,
    NetworkChanged,
    Suspending,
    Resumed
}

public sealed record SystemEvent(SystemEventKind Kind, int? SessionId = null);

/// <summary>
/// Bridges Windows service callbacks, which must return promptly, to the worker loop.
/// Unbounded and non-blocking so a callback never stalls the service control manager.
/// </summary>
public sealed class SystemEventBus
{
    private readonly Channel<SystemEvent> _channel =
        Channel.CreateUnbounded<SystemEvent>(new UnboundedChannelOptions { SingleReader = true });

    public void Publish(SystemEvent systemEvent) => _channel.Writer.TryWrite(systemEvent);

    public ChannelReader<SystemEvent> Reader => _channel.Reader;
}
