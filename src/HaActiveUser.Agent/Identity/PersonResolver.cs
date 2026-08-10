using HaActiveUser.Agent.Configuration;
using HaActiveUser.Agent.Sessions;

namespace HaActiveUser.Agent.Identity;

public sealed record PersonDescriptor(string PersonKey, string DisplayName);

public interface IPersonResolver
{
    /// <summary>Every configured person, in config order. Entities are declared from this, so they exist even when nobody is signed in.</summary>
    IReadOnlyList<PersonDescriptor> KnownPeople { get; }

    /// <summary>Returns null for accounts that are not in the allowlist.</summary>
    string? Resolve(SessionSnapshot session);
}

public sealed class PersonResolver : IPersonResolver
{
    private readonly List<AccountMapping> _mappings;
    private readonly List<PersonDescriptor> _people;

    public PersonResolver(IEnumerable<AccountMapping> mappings)
    {
        _mappings = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.PersonKey))
            .ToList();

        _people = _mappings
            .GroupBy(m => m.PersonKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PersonDescriptor(
                Slug.Make(g.Key),
                g.Select(m => m.DisplayName).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? g.Key))
            .ToList();
    }

    public IReadOnlyList<PersonDescriptor> KnownPeople => _people;

    public string? Resolve(SessionSnapshot session)
    {
        foreach (var mapping in _mappings)
        {
            if (Matches(mapping, session))
            {
                return Slug.Make(mapping.PersonKey);
            }
        }

        return null;
    }

    private static bool Matches(AccountMapping mapping, SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(mapping.Sid)
            && string.Equals(mapping.Sid.Trim(), session.Sid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(mapping.Account))
        {
            return false;
        }

        var configured = mapping.Account.Trim();

        // A bare username in config matches on username alone, so the same entry works on machines
        // where the account is local on one and domain-joined on another.
        return configured.Contains('\\')
            ? string.Equals(configured, session.Account, StringComparison.OrdinalIgnoreCase)
            : string.Equals(configured, session.UserName, StringComparison.OrdinalIgnoreCase);
    }
}
