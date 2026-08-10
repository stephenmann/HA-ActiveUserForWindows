namespace HaActiveUser.Agent.Configuration;

/// <summary>
/// Maps a Windows account to a canonical person. The same human may be
/// <c>DESKTOP-X\stephen</c> on one machine and <c>CORP\sflowers</c> on another; both map to
/// the same <see cref="PersonKey"/> so their entities line up across devices.
/// This list doubles as the tracked-account allowlist.
/// </summary>
public sealed class AccountMapping
{
    /// <summary>Windows SID. Preferred over <see cref="Account"/> because it survives renames.</summary>
    public string? Sid { get; set; }

    /// <summary><c>DOMAIN\user</c>, or a bare username to match on username alone.</summary>
    public string? Account { get; set; }

    public string PersonKey { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public string ResolvedDisplayName => string.IsNullOrWhiteSpace(DisplayName)
        ? PersonKey
        : DisplayName;
}
