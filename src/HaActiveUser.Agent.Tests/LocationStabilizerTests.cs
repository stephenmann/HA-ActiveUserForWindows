using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Location;
using Xunit;

namespace HaActiveUser.Agent.Tests;

public class LocationStabilizerTests
{
    private static (LocationStabilizer Stabilizer, FakeLocationDetector Detector, FakeClock Clock) Create(
        int awayGrace = 120, int resumeSettle = 30)
    {
        var detector = new FakeLocationDetector();
        var clock = new FakeClock(Build.T0);
        var options = new HomeLocationOptions { AwayGraceSeconds = awayGrace, ResumeSettleSeconds = resumeSettle };
        return (new LocationStabilizer(detector, clock, options), detector, clock);
    }

    [Fact]
    public void ArrivingHomeAppliesImmediately()
    {
        var (stabilizer, detector, _) = Create();
        detector.Next = Build.AtHome();

        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);
    }

    [Fact]
    public void LeavingHomeWaitsForTheGracePeriod()
    {
        var (stabilizer, detector, clock) = Create(awayGrace: 120);
        detector.Next = Build.AtHome();
        stabilizer.Update();

        detector.Next = Build.Away();
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(LocationState.Away, stabilizer.Update().State);
    }

    [Fact]
    public void WifiRoamDoesNotFlipTheGate()
    {
        var (stabilizer, detector, clock) = Create(awayGrace: 120);
        detector.Next = Build.AtHome();
        stabilizer.Update();

        // Brief drop while the client re-associates with another access point.
        detector.Next = Build.UnknownLocation();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);

        detector.Next = Build.AtHome();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);
    }

    [Fact]
    public void ResumeFromSleepHoldsThePreviousStateWhileWifiReassociates()
    {
        var (stabilizer, detector, clock) = Create(awayGrace: 0, resumeSettle: 30);
        detector.Next = Build.AtHome();
        stabilizer.Update();

        stabilizer.BeginSettleWindow();
        detector.Next = Build.UnknownLocation();

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(LocationState.Unknown, stabilizer.Update().State);
    }

    [Fact]
    public void ResumeSettleEndsEarlyWhenTheDeviceIsConfirmedHome()
    {
        var (stabilizer, detector, clock) = Create(awayGrace: 0, resumeSettle: 30);
        detector.Next = Build.Away();
        stabilizer.Update();

        stabilizer.BeginSettleWindow();
        detector.Next = Build.AtHome();
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(LocationState.AtHome, stabilizer.Update().State);
    }

    [Fact]
    public void AwayToUnknownTransitionsWithoutWaiting()
    {
        var (stabilizer, detector, _) = Create(awayGrace: 120);
        detector.Next = Build.Away();
        stabilizer.Update();

        detector.Next = Build.UnknownLocation();
        Assert.Equal(LocationState.Unknown, stabilizer.Update().State);
    }
}
