using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HaActiveUser.Agent.Identity;

namespace HaActiveUser.Agent.Mqtt;

/// <summary>
/// Builds a single Home Assistant device-discovery payload describing every entity this machine
/// owns. One retained message per device keeps entity churn out of the broker and makes removal a
/// matter of publishing one empty payload.
/// </summary>
public sealed class DiscoveryPayloadBuilder
{
    private const string Manufacturer = "HA Active User for Windows";
    private const string OriginName = "ha-activeuser-windows";
    private const string OriginUrl = "https://github.com/stephenmann/HA-ActiveUserForWindows";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly MqttTopics _topics;
    private readonly DeviceIdentity _device;
    private readonly string _suggestedArea;

    public DiscoveryPayloadBuilder(MqttTopics topics, DeviceIdentity device, string suggestedArea)
    {
        _topics = topics;
        _device = device;
        _suggestedArea = suggestedArea;
    }

    public string Build(IReadOnlyList<PersonDescriptor> people)
    {
        var payload = new JsonObject
        {
            ["dev"] = new JsonObject
            {
                ["ids"] = new JsonArray(_device.DeviceIdentifier),
                ["name"] = _device.DeviceName,
                ["mf"] = Manufacturer,
                ["mdl"] = Environment.OSVersion.VersionString,
                ["sw"] = Version,
                // suggested_area is only a creation-time hint; moving the device later is done in HA.
                ["sa"] = _suggestedArea
            },
            ["o"] = new JsonObject
            {
                ["name"] = OriginName,
                ["sw"] = Version,
                ["url"] = OriginUrl
            },
            // Device discovery only honours a fixed set of shared root options, and "~" is not one of
            // them, so component topics must be fully qualified.
            ["avty_t"] = _topics.Availability,
            ["pl_avail"] = MqttTopics.Online,
            ["pl_not_avail"] = MqttTopics.Offline,
            ["qos"] = 1,
            ["cmps"] = BuildComponents(people)
        };

        return payload.ToJsonString(SerializerOptions);
    }

    /// <summary>An empty retained payload on the discovery topic deletes the device and its entities.</summary>
    public static string BuildRemoval() => string.Empty;

    private JsonObject BuildComponents(IReadOnlyList<PersonDescriptor> people)
    {
        var components = new JsonObject
        {
            ["active_user"] = Sensor(
                key: "active_user",
                name: "Active user",
                stateTopic: _topics.ActiveUser,
                icon: "mdi:account",
                diagnostic: true),

            ["at_home"] = BinarySensor(
                key: "at_home",
                name: "At home location",
                stateTopic: _topics.AtHome,
                deviceClass: null,
                icon: "mdi:home-map-marker",
                diagnostic: true),

            ["network_location"] = Sensor(
                key: "network_location",
                name: "Network location",
                stateTopic: _topics.NetworkLocation,
                icon: "mdi:map-marker-radius",
                diagnostic: true)
        };

        foreach (var person in people)
        {
            var attributes = _topics.Attributes(person.PersonKey);

            components[$"{person.PersonKey}_occupancy"] = BinarySensor(
                key: $"{person.PersonKey}_occupancy",
                name: $"{person.DisplayName} occupancy",
                stateTopic: _topics.Occupancy(person.PersonKey),
                deviceClass: "occupancy",
                icon: null,
                diagnostic: false,
                attributesTopic: attributes);

            components[$"{person.PersonKey}_room"] = Sensor(
                key: $"{person.PersonKey}_room",
                name: $"{person.DisplayName} room",
                stateTopic: _topics.Room(person.PersonKey),
                icon: "mdi:floor-plan",
                diagnostic: false,
                attributesTopic: attributes);

            components[$"{person.PersonKey}_locked"] = BinarySensor(
                key: $"{person.PersonKey}_locked",
                name: $"{person.DisplayName} screen locked",
                stateTopic: _topics.Locked(person.PersonKey),
                deviceClass: null,
                icon: "mdi:lock",
                diagnostic: true);

            var idle = Sensor(
                key: $"{person.PersonKey}_idle",
                name: $"{person.DisplayName} idle time",
                stateTopic: _topics.Idle(person.PersonKey),
                icon: "mdi:timer-sand",
                diagnostic: true);

            idle["dev_cla"] = "duration";
            idle["stat_cla"] = "measurement";
            idle["unit_of_meas"] = "s";
            components[$"{person.PersonKey}_idle"] = idle;
        }

        return components;
    }

    private JsonObject Sensor(
        string key,
        string name,
        string stateTopic,
        string? icon,
        bool diagnostic,
        string? attributesTopic = null)
    {
        var component = new JsonObject
        {
            ["p"] = "sensor",
            ["name"] = name,
            ["uniq_id"] = UniqueId(key),
            ["stat_t"] = stateTopic
        };

        Decorate(component, icon, diagnostic, attributesTopic);
        return component;
    }

    private JsonObject BinarySensor(
        string key,
        string name,
        string stateTopic,
        string? deviceClass,
        string? icon,
        bool diagnostic,
        string? attributesTopic = null)
    {
        var component = new JsonObject
        {
            ["p"] = "binary_sensor",
            ["name"] = name,
            ["uniq_id"] = UniqueId(key),
            ["stat_t"] = stateTopic,
            ["pl_on"] = "ON",
            ["pl_off"] = "OFF"
        };

        if (deviceClass is not null)
        {
            component["dev_cla"] = deviceClass;
        }

        Decorate(component, icon, diagnostic, attributesTopic);
        return component;
    }

    private static void Decorate(JsonObject component, string? icon, bool diagnostic, string? attributesTopic)
    {
        if (icon is not null)
        {
            component["ic"] = icon;
        }

        if (diagnostic)
        {
            component["ent_cat"] = "diagnostic";
        }

        if (attributesTopic is not null)
        {
            component["json_attr_t"] = attributesTopic;
        }
    }

    private string UniqueId(string key) => $"haau_{_device.DeviceId}_{key}";
}
