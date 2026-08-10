namespace HaActiveUser.Agent.Location;

public enum LocationState
{
    Unknown,
    Away,
    AtHome
}

/// <param name="Matched">Null when the strategy cannot tell, e.g. no Wi-Fi adapter present.</param>
/// <param name="Room">Room resolved by the strategy. v1 always yields the configured home-base room.</param>
public sealed record LocationProbe(bool? Matched, string? Detail, string? Room = null)
{
    public static LocationProbe Indeterminate(string detail) => new(null, detail);
}

public sealed record LocationReading(LocationState State, string? Room, string Label, string? RawDetail);

public interface ILocationStrategy
{
    string Name { get; }

    LocationProbe Probe();
}

public interface IHomeLocationDetector
{
    LocationReading Read();
}
