using HaActiveUser.Agent.Identity;

namespace HaActiveUser.Agent.Mqtt;

public sealed class MqttTopics
{
    public MqttTopics(string topicPrefix, string discoveryPrefix, DeviceIdentity device)
    {
        Base = $"{Slug.Make(topicPrefix)}/{device.DeviceId}";
        Discovery = $"{Slug.Make(discoveryPrefix)}/device/{device.DiscoveryObjectId}/config";
    }

    /// <summary>Published as <c>~</c> in the discovery payload so component topics stay short.</summary>
    public string Base { get; }

    public string Discovery { get; }

    public string Availability => $"{Base}/status";

    public string ActiveUser => $"{Base}/active_user";

    public string AtHome => $"{Base}/at_home";

    public string NetworkLocation => $"{Base}/network_location";

    public string PersonBase(string personKey) => $"{Base}/person/{personKey}";

    public string Occupancy(string personKey) => $"{PersonBase(personKey)}/occupancy";

    public string Room(string personKey) => $"{PersonBase(personKey)}/room";

    public string Locked(string personKey) => $"{PersonBase(personKey)}/locked";

    public string Idle(string personKey) => $"{PersonBase(personKey)}/idle";

    public string Attributes(string personKey) => $"{PersonBase(personKey)}/attributes";

    public const string HomeAssistantStatus = "homeassistant/status";

    public const string Online = "online";

    public const string Offline = "offline";
}
