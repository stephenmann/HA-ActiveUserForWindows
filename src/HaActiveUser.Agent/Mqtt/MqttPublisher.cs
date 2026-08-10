using System.Security.Cryptography.X509Certificates;
using System.Text;
using HaActiveUser.Agent.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HaActiveUser.Agent.Mqtt;

public interface IMqttPublisher
{
    bool IsConnected { get; }

    event Func<CancellationToken, Task>? BrokerConnected;

    event Func<CancellationToken, Task>? HomeAssistantRestarted;

    Task StartAsync(CancellationToken cancellationToken);

    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken);

    Task GoOfflineAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class MqttPublisher : IMqttPublisher, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly MqttTopics _topics;
    private readonly ISecretProtector _secrets;
    private readonly ILogger<MqttPublisher> _logger;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private CancellationTokenSource? _lifetime;

    public MqttPublisher(
        MqttOptions options,
        MqttTopics topics,
        string clientId,
        ISecretProtector secrets,
        ILogger<MqttPublisher> logger)
    {
        _options = options;
        _topics = topics;
        _secrets = secrets;
        _logger = logger;
        _client = new MqttFactory().CreateMqttClient();
        _clientOptions = BuildClientOptions(clientId);

        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public event Func<CancellationToken, Task>? BrokerConnected;

    public event Func<CancellationToken, Task>? HomeAssistantRestarted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ConnectAsync(_lifetime.Token).ConfigureAwait(false);
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        try
        {
            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Publish to {Topic} failed", topic);
        }
    }

    /// <summary>Publishes the offline marker explicitly, for a clean shutdown or an impending suspend.</summary>
    public Task GoOfflineAsync(CancellationToken cancellationToken) =>
        PublishAsync(_topics.Availability, MqttTopics.Offline, retain: true, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await GoOfflineAsync(cancellationToken).ConfigureAwait(false);
            await _client.DisconnectAsync(
                new MqttClientDisconnectOptionsBuilder()
                    .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                    .Build(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during MQTT shutdown");
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client.IsConnected)
            {
                return;
            }

            await _client.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", _options.Host, _options.Port);

            await _client.SubscribeAsync(
                MqttTopics.HomeAssistantStatus,
                MqttQualityOfServiceLevel.AtLeastOnce,
                cancellationToken).ConfigureAwait(false);

            await PublishAsync(_topics.Availability, MqttTopics.Online, retain: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }

        if (BrokerConnected is { } handler)
        {
            await handler(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
        {
            return;
        }

        _logger.LogWarning(
            args.Exception, "Disconnected from MQTT broker ({Reason}); reconnecting", args.Reason);

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectDelaySeconds)), token)
                    .ConfigureAwait(false);
                await ConnectAsync(token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reconnect attempt failed");
            }
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (!string.Equals(args.ApplicationMessage.Topic, MqttTopics.HomeAssistantStatus, StringComparison.Ordinal))
        {
            return;
        }

        var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
        if (!string.Equals(payload, MqttTopics.Online, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _logger.LogInformation("Home Assistant came online; republishing discovery");

        var token = _lifetime?.Token ?? CancellationToken.None;
        if (HomeAssistantRestarted is { } handler)
        {
            try
            {
                await handler(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Republishing discovery after Home Assistant restart failed");
            }
        }
    }

    private MqttClientOptions BuildClientOptions(string clientId)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(5, _options.KeepAliveSeconds)))
            .WithCleanSession()
            // The will is what tells Home Assistant the machine dropped off ungracefully.
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(Encoding.UTF8.GetBytes(MqttTopics.Offline))
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            builder = builder.WithCredentials(_options.Username, _secrets.Unprotect(_options.ProtectedPassword) ?? string.Empty);
        }

        if (_options.Tls.Enabled)
        {
            builder = builder.WithTlsOptions(tls => ConfigureTls(tls));
        }

        return builder.Build();
    }

    private void ConfigureTls(MqttClientTlsOptionsBuilder tls)
    {
        tls.UseTls()
            .WithAllowUntrustedCertificates(_options.Tls.AllowUntrustedCertificates)
            .WithIgnoreCertificateChainErrors(_options.Tls.IgnoreCertificateChainErrors)
            .WithIgnoreCertificateRevocationErrors(_options.Tls.IgnoreCertificateRevocationErrors);

        var certificates = new X509Certificate2Collection();

        if (!string.IsNullOrWhiteSpace(_options.Tls.CaCertificatePath))
        {
            certificates.Add(new X509Certificate2(_options.Tls.CaCertificatePath));
        }

        if (!string.IsNullOrWhiteSpace(_options.Tls.ClientCertificatePath))
        {
            var password = _secrets.Unprotect(_options.Tls.ProtectedClientCertificatePassword);
            certificates.Add(new X509Certificate2(_options.Tls.ClientCertificatePath, password));
        }

        if (certificates.Count > 0)
        {
            tls.WithClientCertificates(certificates);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Dispose();
        _connectGate.Dispose();
        _client.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
