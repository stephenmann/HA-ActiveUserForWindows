namespace HaActiveUser.Agent.Presence;

/// <param name="Room">Resolved room, or <c>away</c> / <c>unknown</c> when the location gate says the device is not at home.</param>
/// <param name="LastActiveUtc">Used by Home Assistant templates to break ties when several devices report the same person.</param>
public sealed record PresenceState(
    string PersonKey,
    string DisplayName,
    bool IsOccupied,
    bool IsSignedIn,
    bool IsLocked,
    int IdleSeconds,
    string Room,
    DateTimeOffset? LastActiveUtc,
    string? SourceAccount,
    int? SessionId);

public static class RoomNames
{
    public const string Away = "away";
    public const string Unknown = "unknown";
}
