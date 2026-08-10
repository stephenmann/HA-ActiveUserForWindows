using System.Text;

namespace HaActiveUser.Agent.Identity;

/// <summary>
/// Home Assistant restricts discovery object IDs to [a-zA-Z0-9_-], and the same constraint keeps
/// MQTT topics free of wildcards and separators.
/// </summary>
public static class Slug
{
    public static string Make(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "unknown" : slug;
    }
}
