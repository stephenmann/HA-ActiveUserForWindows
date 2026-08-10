using System.Text;

namespace HaActiveUser.Agent.Location;

public static class MacFormat
{
    public static string Normalise(IReadOnlyList<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Count * 3);
        for (var i = 0; i < bytes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(bytes[i].ToString("x2"));
        }

        return builder.ToString();
    }

    /// <summary>Compares MACs ignoring separators, so config can use colons, hyphens or nothing.</summary>
    public static bool Equal(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(Strip(left), Strip(right), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
