namespace HaActiveUser.Agent.Sessions;

/// <summary>Mirrors WTS_CONNECTSTATE_CLASS.</summary>
public enum WtsConnectState
{
    Active = 0,
    Connected = 1,
    ConnectQuery = 2,
    Shadow = 3,
    Disconnected = 4,
    Idle = 5,
    Listen = 6,
    Reset = 7,
    Down = 8,
    Init = 9
}

public sealed record SessionSnapshot
{
    public required int SessionId { get; init; }

    public string? Sid { get; init; }

    public string? Domain { get; init; }

    public string? UserName { get; init; }

    public required WtsConnectState ConnectState { get; init; }

    public required bool IsLocked { get; init; }

    public DateTimeOffset? LastInputUtc { get; init; }

    public bool IsRemote { get; init; }

    public string? Account => string.IsNullOrEmpty(UserName)
        ? null
        : string.IsNullOrEmpty(Domain) ? UserName : $"{Domain}\\{UserName}";

    /// <summary>A session the user is attached to. Disconnected RDP sessions keep running but nobody is at them.</summary>
    public bool IsAttached => ConnectState == WtsConnectState.Active;
}
