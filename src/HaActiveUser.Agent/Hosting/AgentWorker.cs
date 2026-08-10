using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Threading.Channels;
using HaActiveUser.Agent.Abstractions;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.DeviceProfiles;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Mqtt;
using HaActiveUser.Agent.Presence;
using HaActiveUser.Agent.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaActiveUser.Agent.Hosting;

[SupportedOSPlatform("windows")]
public sealed class AgentWorker : BackgroundService
{
    private readonly ISessionProvider _sessions;
    private readonly LocationStabilizer _location;
    private readonly OccupancyEvaluator _evaluator;
    private readonly StatePublisher _statePublisher;
    private readonly IMqttPublisher _mqtt;
    private readonly SystemEventBus _events;
    private readonly IClock _clock;
    private readonly DeviceProfile _profile;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        ISessionProvider sessions,
        LocationStabilizer location,
        OccupancyEvaluator evaluator,
        StatePublisher statePublisher,
        IMqttPublisher mqtt,
        SystemEventBus events,
        IClock clock,
        IDeviceProfileDetector profileDetector,
        IOptions<AgentOptions> options,
        ILogger<AgentWorker> logger)
    {
        _sessions = sessions;
        _location = location;
        _evaluator = evaluator;
        _statePublisher = statePublisher;
        _mqtt = mqtt;
        _events = events;
        _clock = clock;
        _profile = profileDetector.Detect();
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqtt.BrokerConnected += RepublishAsync;
        _mqtt.HomeAssistantRestarted += RepublishAfterDelayAsync;

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // Cover the case where the service starts while the machine is still bringing networking up.
        _location.BeginSettleWindow();

        try
        {
            await _mqtt.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Initial MQTT connection failed; the reconnect loop will keep trying");
        }

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
                await WaitForNextTickAsync(pollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _mqtt.BrokerConnected -= RepublishAsync;
            _mqtt.HomeAssistantRestarted -= RepublishAfterDelayAsync;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _mqtt.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var location = _location.Update();
            var sessions = _sessions.GetSessions();

            var states = _evaluator.Evaluate(
                new EvaluationInput(sessions, location, _profile, _clock.UtcNow));

            await _statePublisher.PublishAsync(states, location, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Evaluation tick failed");
        }
    }

    /// <summary>Wakes early when Windows reports something that could change presence.</summary>
    private async Task WaitForNextTickAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(pollInterval);

        try
        {
            var systemEvent = await _events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            await HandleAsync(systemEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Poll interval elapsed with no events.
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task HandleAsync(SystemEvent systemEvent, CancellationToken cancellationToken)
    {
        switch (systemEvent.Kind)
        {
            case SystemEventKind.Suspending:
                // Announce the outage before the NIC dies, otherwise HA waits for the will to expire.
                await _mqtt.GoOfflineAsync(cancellationToken).ConfigureAwait(false);
                break;

            case SystemEventKind.Resumed:
                // Wi-Fi re-associates several seconds after wake; without this the agent would
                // publish a spurious "away" on every resume.
                _location.BeginSettleWindow();
                break;
        }

        _logger.LogDebug("Handled {Kind} for session {SessionId}", systemEvent.Kind, systemEvent.SessionId);
    }

    private async Task RepublishAsync(CancellationToken cancellationToken)
    {
        await _statePublisher.PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        await TickAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RepublishAfterDelayAsync(CancellationToken cancellationToken)
    {
        // Every agent on the network sees the same birth message; stagger to avoid a thundering herd.
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(500, 5000)), cancellationToken)
            .ConfigureAwait(false);
        await RepublishAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _events.Publish(new SystemEvent(SystemEventKind.NetworkChanged));

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        _events.Publish(new SystemEvent(SystemEventKind.NetworkChanged));
}
