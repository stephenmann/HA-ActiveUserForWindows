namespace HaActiveUser.Agent.Configuration;

public sealed class MqttOptions
{
    public string Host { get; set; } = "homeassistant.local";

    public int Port { get; set; } = 1883;

    public string? ClientId { get; set; }

    public string? Username { get; set; }

    /// <summary>DPAPI-protected (LocalMachine scope), base64. Written by the <c>set-password</c> verb.</summary>
    public string? ProtectedPassword { get; set; }

    public TlsOptions Tls { get; set; } = new();

    public int ReconnectDelaySeconds { get; set; } = 5;

    public int KeepAliveSeconds { get; set; } = 60;
}

public sealed class TlsOptions
{
    public bool Enabled { get; set; }

    /// <summary>PEM or DER CA certificate for a self-signed broker.</summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>PFX containing the client certificate and key, for mTLS.</summary>
    public string? ClientCertificatePath { get; set; }

    public string? ProtectedClientCertificatePassword { get; set; }

    public bool AllowUntrustedCertificates { get; set; }

    public bool IgnoreCertificateChainErrors { get; set; }

    public bool IgnoreCertificateRevocationErrors { get; set; }
}
