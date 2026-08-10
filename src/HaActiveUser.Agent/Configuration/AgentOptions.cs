namespace HaActiveUser.Agent.Configuration;

public enum DeviceProfileSetting
{
    Auto,
    Desktop,
    Laptop
}

public enum LocationMatchMode
{
    Any,
    All
}

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Home-base room. Becomes the device's <c>suggested_area</c> and the default room name.</summary>
    public string Room { get; set; } = "Office";

    public DeviceProfileSetting DeviceProfile { get; set; } = DeviceProfileSetting.Auto;

    public string DiscoveryPrefix { get; set; } = "homeassistant";

    public string TopicPrefix { get; set; } = "haactiveuser";

    /// <summary>Overrides the auto-detected machine name shown in Home Assistant.</summary>
    public string? DeviceName { get; set; }

    public int IdleThresholdSeconds { get; set; } = 600;

    /// <summary>How long occupancy is held after the last input, to stop lock/idle blips flapping.</summary>
    public int AwayGraceSeconds { get; set; } = 60;

    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Minimum seconds between idle-sensor publishes when nothing else changed.</summary>
    public int IdleHeartbeatSeconds { get; set; } = 60;

    public List<AccountMapping> Accounts { get; set; } = [];

    public HomeLocationOptions HomeLocation { get; set; } = new();

    public MqttOptions Mqtt { get; set; } = new();
}
