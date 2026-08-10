using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Location;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HaActiveUser.Agent.Tests;

file sealed class StubStrategy(string name, LocationProbe probe) : ILocationStrategy
{
    public string Name { get; } = name;

    public LocationProbe Probe() => probe;
}

file sealed class ThrowingStrategy : ILocationStrategy
{
    public string Name => "boom";

    public LocationProbe Probe() => throw new InvalidOperationException("driver unavailable");
}

public class CompositeHomeLocationDetectorTests
{
    private static CompositeHomeLocationDetector Create(
        LocationMatchMode mode, bool publishRaw, params ILocationStrategy[] strategies) =>
        new(strategies, mode, "Office", publishRaw, NullLogger<CompositeHomeLocationDetector>.Instance);

    [Fact]
    public void NoStrategiesMeansUnknown()
    {
        var reading = Create(LocationMatchMode.Any, false).Read();

        Assert.Equal(LocationState.Unknown, reading.State);
    }

    [Fact]
    public void AnyModeMatchesOnASingleStrategy()
    {
        var detector = Create(
            LocationMatchMode.Any,
            false,
            new StubStrategy("wifi", new LocationProbe(false, "elsewhere")),
            new StubStrategy("dock", new LocationProbe(true, "docked", "Office")));

        var reading = detector.Read();

        Assert.Equal(LocationState.AtHome, reading.State);
        Assert.Equal("Office", reading.Room);
    }

    [Fact]
    public void AllModeRequiresEveryDecidedStrategy()
    {
        var detector = Create(
            LocationMatchMode.All,
            false,
            new StubStrategy("wifi", new LocationProbe(false, "elsewhere")),
            new StubStrategy("dock", new LocationProbe(true, "docked", "Office")));

        Assert.Equal(LocationState.Away, detector.Read().State);
    }

    [Fact]
    public void IndeterminateStrategiesAreExcludedFromTheDecision()
    {
        var detector = Create(
            LocationMatchMode.All,
            false,
            LocationProbeStrategy(),
            new StubStrategy("dock", new LocationProbe(true, "docked", "Office")));

        Assert.Equal(LocationState.AtHome, detector.Read().State);

        static ILocationStrategy LocationProbeStrategy() =>
            new StubStrategy("wifi", LocationProbe.Indeterminate("no adapter"));
    }

    [Fact]
    public void AllIndeterminateMeansUnknownRatherThanAway()
    {
        var detector = Create(
            LocationMatchMode.Any,
            false,
            new StubStrategy("wifi", LocationProbe.Indeterminate("no adapter")));

        Assert.Equal(LocationState.Unknown, detector.Read().State);
    }

    [Fact]
    public void AFailingStrategyIsTreatedAsIndeterminate()
    {
        var detector = Create(LocationMatchMode.Any, false, new ThrowingStrategy());

        Assert.Equal(LocationState.Unknown, detector.Read().State);
    }

    [Fact]
    public void RawIdentifiersAreWithheldByDefault()
    {
        var strategy = new StubStrategy("wifi", new LocationProbe(true, "bssid=aa:bb:cc:dd:ee:ff", "Office"));

        Assert.Null(Create(LocationMatchMode.Any, false, strategy).Read().RawDetail);
        Assert.Contains("aa:bb:cc", Create(LocationMatchMode.Any, true, strategy).Read().RawDetail);
    }
}

public class WifiLocationStrategyTests
{
    [Fact]
    public void UnconfiguredStrategyIsIndeterminate()
    {
        var strategy = new WifiLocationStrategy(
            new WifiMatchOptions(), "Office", NullLogger<WifiLocationStrategy>.Instance);

        Assert.Null(strategy.Probe().Matched);
    }
}

public class DockLocationStrategyTests
{
    [Fact]
    public void UnconfiguredStrategyIsIndeterminate()
    {
        var strategy = new DockLocationStrategy([], "Office", NullLogger<DockLocationStrategy>.Instance);

        Assert.Null(strategy.Probe().Matched);
    }
}

public class GatewayMacLocationStrategyTests
{
    [Fact]
    public void UnconfiguredStrategyIsIndeterminate()
    {
        var strategy = new GatewayMacLocationStrategy([], "Office", NullLogger<GatewayMacLocationStrategy>.Instance);

        Assert.Null(strategy.Probe().Matched);
    }
}
