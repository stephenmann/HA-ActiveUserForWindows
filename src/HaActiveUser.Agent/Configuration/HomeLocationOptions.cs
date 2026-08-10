namespace HaActiveUser.Agent.Configuration;

public sealed class HomeLocationOptions
{
    /// <summary>
    /// When null, the gate is required for laptops and skipped for desktops.
    /// Set explicitly to force either behaviour.
    /// </summary>
    public bool? RequireForOccupancy { get; set; }

    public LocationMatchMode MatchMode { get; set; } = LocationMatchMode.Any;

    public WifiMatchOptions Wifi { get; set; } = new();

    /// <summary>MAC addresses of the home default gateway, any separator or none.</summary>
    public List<string> GatewayMacs { get; set; } = [];

    /// <summary>Device instance IDs of a dock or fixed monitor, matched as a prefix.</summary>
    public List<string> DockDeviceIds { get; set; } = [];

    /// <summary>Seconds of continuous "away" before the gate actually flips off.</summary>
    public int AwayGraceSeconds { get; set; } = 120;

    /// <summary>Grace after resume-from-sleep, during which the previous location is held.</summary>
    public int ResumeSettleSeconds { get; set; } = 30;

    /// <summary>Publish raw BSSIDs and MACs to Home Assistant. Off by default: the recorder keeps them forever.</summary>
    public bool PublishRawIdentifiers { get; set; }

    public bool HasAnyStrategyConfigured =>
        Wifi.Bssids.Count > 0 || Wifi.Ssids.Count > 0 || GatewayMacs.Count > 0 || DockDeviceIds.Count > 0;
}

public sealed class WifiMatchOptions
{
    /// <summary>Preferred: identifies the actual access point rather than a spoofable name.</summary>
    public List<string> Bssids { get; set; } = [];

    public List<string> Ssids { get; set; } = [];
}
