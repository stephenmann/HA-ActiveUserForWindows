using HaActiveUser.Agent.Abstractions;
using HaActiveUser.Agent.Configuration;

namespace HaActiveUser.Agent.Location;

/// <summary>
/// Debounces raw location readings.
/// <para>
/// Two real-world effects make the raw reading untrustworthy for short windows. Waking from sleep
/// leaves the Wi-Fi radio unassociated for several seconds, which would otherwise publish a false
/// "away" on every resume. Roaming between access points briefly drops the association, which would
/// otherwise flap. Becoming home is therefore applied immediately, but leaving home has to persist.
/// </para>
/// </summary>
public sealed class LocationStabilizer
{
    private readonly IHomeLocationDetector _detector;
    private readonly IClock _clock;
    private readonly TimeSpan _awayGrace;
    private readonly TimeSpan _resumeSettle;

    private LocationReading _stable = new(LocationState.Unknown, null, "unknown", null);
    private DateTimeOffset? _leftHomeAt;
    private DateTimeOffset? _settleUntil;

    public LocationStabilizer(IHomeLocationDetector detector, IClock clock, HomeLocationOptions options)
    {
        _detector = detector;
        _clock = clock;
        _awayGrace = TimeSpan.FromSeconds(Math.Max(0, options.AwayGraceSeconds));
        _resumeSettle = TimeSpan.FromSeconds(Math.Max(0, options.ResumeSettleSeconds));
    }

    public LocationReading Current => _stable;

    /// <summary>Called on resume from sleep and on service start.</summary>
    public void BeginSettleWindow() => _settleUntil = _clock.UtcNow + _resumeSettle;

    public LocationReading Update()
    {
        var raw = _detector.Read();
        var now = _clock.UtcNow;

        if (raw.State == LocationState.AtHome)
        {
            _leftHomeAt = null;
            _settleUntil = null;
            _stable = raw;
            return _stable;
        }

        if (_settleUntil is { } settleUntil && now < settleUntil)
        {
            return _stable;
        }

        _settleUntil = null;

        if (_stable.State != LocationState.AtHome)
        {
            _leftHomeAt = null;
            _stable = raw;
            return _stable;
        }

        _leftHomeAt ??= now;
        if (now - _leftHomeAt.Value >= _awayGrace)
        {
            _leftHomeAt = null;
            _stable = raw;
        }

        return _stable;
    }
}
