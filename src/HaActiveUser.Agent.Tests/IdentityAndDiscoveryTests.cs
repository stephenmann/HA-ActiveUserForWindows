using System.Text.Json;
using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Identity;
using HaActiveUser.Agent.Location;
using HaActiveUser.Agent.Mqtt;
using HaActiveUser.Agent.Sessions;
using Xunit;

namespace HaActiveUser.Agent.Tests;

public class PersonResolverTests
{
    [Fact]
    public void SidMatchWinsOverAccountName()
    {
        var resolver = new PersonResolver([
            new AccountMapping { Sid = "S-1-5-21-1-1-1-1001", PersonKey = "stephen", DisplayName = "Stephen" }
        ]);

        Assert.Equal("stephen", resolver.Resolve(Build.Session(user: "renamed")));
    }

    [Fact]
    public void BareUsernameMatchesAnyDomain()
    {
        var resolver = new PersonResolver([
            new AccountMapping { Account = "sflowers", PersonKey = "stephen" }
        ]);

        Assert.Equal("stephen", resolver.Resolve(Build.Session(user: "sflowers", domain: "CORP", sid: null)));
        Assert.Equal("stephen", resolver.Resolve(Build.Session(user: "sflowers", domain: "LAPTOP", sid: null)));
    }

    [Fact]
    public void QualifiedAccountRequiresTheDomain()
    {
        var resolver = new PersonResolver([
            new AccountMapping { Account = "CORP\\sflowers", PersonKey = "stephen" }
        ]);

        Assert.Equal("stephen", resolver.Resolve(Build.Session(user: "sflowers", domain: "CORP", sid: null)));
        Assert.Null(resolver.Resolve(Build.Session(user: "sflowers", domain: "OTHER", sid: null)));
    }

    [Fact]
    public void PersonKeysAreSluggedAndDeduplicated()
    {
        var resolver = new PersonResolver([
            new AccountMapping { Account = "a", PersonKey = "Stephen Flowers", DisplayName = "Stephen" },
            new AccountMapping { Account = "b", PersonKey = "Stephen Flowers" }
        ]);

        var person = Assert.Single(resolver.KnownPeople);
        Assert.Equal("stephen_flowers", person.PersonKey);
        Assert.Equal("Stephen", person.DisplayName);
    }

    [Fact]
    public void DisplayNameFallsBackToThePersonKey()
    {
        var resolver = new PersonResolver([new AccountMapping { Account = "a", PersonKey = "guest" }]);

        Assert.Equal("guest", Assert.Single(resolver.KnownPeople).DisplayName);
    }

    [Fact]
    public void UnmappedAccountsResolveToNull()
    {
        var resolver = new PersonResolver([new AccountMapping { Account = "someone", PersonKey = "someone" }]);

        Assert.Null(resolver.Resolve(Build.Session(user: "nobody", sid: null)));
    }
}

public class SlugTests
{
    [Theory]
    [InlineData("Stephen Flowers", "stephen_flowers")]
    [InlineData("CORP\\sflowers", "corp_sflowers")]
    [InlineData("{4b0e2f1a-1111}", "4b0e2f1a-1111")]
    [InlineData("   ", "unknown")]
    [InlineData("!!!", "unknown")]
    public void ProducesDiscoverySafeIdentifiers(string input, string expected) =>
        Assert.Equal(expected, Slug.Make(input));
}

public class MacFormatTests
{
    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AA-BB-CC-DD-EE-FF")]
    [InlineData("aabbccddeeff", "aa:bb:cc:dd:ee:ff")]
    public void IgnoresSeparatorsAndCase(string left, string right) => Assert.True(MacFormat.Equal(left, right));

    [Fact]
    public void DifferentAddressesDoNotMatch() =>
        Assert.False(MacFormat.Equal("aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:00"));

    [Fact]
    public void NullsNeverMatch() => Assert.False(MacFormat.Equal(null, "aabbccddeeff"));

    [Fact]
    public void FormatsBytesAsLowercaseColonSeparated() =>
        Assert.Equal("0a:1b:2c:3d:4e:5f", MacFormat.Normalise([0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f]));
}

public class DiscoveryPayloadBuilderTests
{
    private static JsonElement BuildPayload()
    {
        var device = new DeviceIdentity("abc123", "OFFICE-PC");
        var topics = new MqttTopics("haactiveuser", "homeassistant", device);
        var builder = new DiscoveryPayloadBuilder(topics, device, "Office");

        return JsonDocument.Parse(builder.Build([new PersonDescriptor("stephen", "Stephen")])).RootElement.Clone();
    }

    [Fact]
    public void IncludesTheDeviceAndOriginBlocksRequiredByDeviceDiscovery()
    {
        var payload = BuildPayload();

        Assert.Equal("haau_abc123", payload.GetProperty("dev").GetProperty("ids")[0].GetString());
        Assert.Equal("Office", payload.GetProperty("dev").GetProperty("sa").GetString());
        Assert.Equal("ha-activeuser-windows", payload.GetProperty("o").GetProperty("name").GetString());
    }

    [Fact]
    public void UsesTheBaseTopicAbbreviation()
    {
        var payload = BuildPayload();

        Assert.Equal("haactiveuser/abc123", payload.GetProperty("~").GetString());
        Assert.Equal("~/status", payload.GetProperty("avty_t").GetString());
    }

    [Fact]
    public void EveryComponentDeclaresPlatformAndUniqueId()
    {
        var payload = BuildPayload();

        foreach (var component in payload.GetProperty("cmps").EnumerateObject())
        {
            Assert.False(string.IsNullOrEmpty(component.Value.GetProperty("p").GetString()));
            Assert.StartsWith("haau_abc123_", component.Value.GetProperty("uniq_id").GetString());
        }
    }

    [Fact]
    public void OccupancyIsABinarySensorWithTheOccupancyDeviceClass()
    {
        var occupancy = BuildPayload().GetProperty("cmps").GetProperty("stephen_occupancy");

        Assert.Equal("binary_sensor", occupancy.GetProperty("p").GetString());
        Assert.Equal("occupancy", occupancy.GetProperty("dev_cla").GetString());
        Assert.Equal("~/person/stephen/occupancy", occupancy.GetProperty("stat_t").GetString());
        Assert.Equal("~/person/stephen/attributes", occupancy.GetProperty("json_attr_t").GetString());
    }

    [Fact]
    public void RemovalPayloadIsEmptySoHomeAssistantDeletesTheDevice() =>
        Assert.Equal(string.Empty, DiscoveryPayloadBuilder.BuildRemoval());
}

public class SessionSnapshotTests
{
    [Fact]
    public void AccountCombinesDomainAndUser() =>
        Assert.Equal("CORP\\sflowers", Build.Session(user: "sflowers", domain: "CORP").Account);

    [Fact]
    public void AccountOmitsAnEmptyDomain() =>
        Assert.Equal("stephen", Build.Session(user: "stephen", domain: "").Account);

    [Theory]
    [InlineData(WtsConnectState.Active, true)]
    [InlineData(WtsConnectState.Disconnected, false)]
    [InlineData(WtsConnectState.Idle, false)]
    public void OnlyActiveSessionsAreAttached(WtsConnectState state, bool expected) =>
        Assert.Equal(expected, Build.Session(state: state).IsAttached);
}
