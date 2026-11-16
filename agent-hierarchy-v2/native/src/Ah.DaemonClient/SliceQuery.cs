using System.Globalization;
using System.Text;

namespace Ah.DaemonClient;

/// <summary>
/// The query of one slice-feed poll, built once for every client: the terminal view and <c>ah view events</c>. Each caller appends it
/// to the literal route <c>/v1/view?path=%2Fevents</c> at its own call, so the transport scan still sees the route.
/// </summary>
internal static class SliceQuery
{
    /// <summary>The query after the route: <c>since</c>, <c>epoch</c> when known, <c>wait</c>, <c>sub</c>, and <c>fresh</c> when any.</summary>
    public static string Events(IReadOnlyCollection<string> sub, IReadOnlyCollection<string> fresh, long since, string? epoch, int waitSeconds) =>
        "&since=" + since.ToString(CultureInfo.InvariantCulture)
        + (epoch is null ? string.Empty : "&epoch=" + FormEncode(epoch))
        + "&wait=" + waitSeconds.ToString(CultureInfo.InvariantCulture)
        + "&sub=" + FormEncode(string.Join(',', sub))
        + (fresh.Count == 0 ? string.Empty : "&fresh=" + FormEncode(string.Join(',', fresh)));

    /// <summary><paramref name="value"/> as a URL's form encoding makes it.</summary>
    public static string FormEncode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '*' or '-' or '.' or '_')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}
