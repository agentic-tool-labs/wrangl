using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>Matches <c>METHOD /path</c> patterns, with <c>{name}</c> path parameters, against requests.</summary>
internal sealed class Router
{
    private readonly List<Entry> _entries = [];

    internal Router(IEnumerable<RouteRegistration> routes)
    {
        foreach (var route in routes)
        {
            var space = route.Pattern.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                throw new ArgumentException($"route pattern '{route.Pattern}' is not 'METHOD /path'", nameof(routes));
            }

            var method = route.Pattern[..space];
            var path = route.Pattern[(space + 1)..];
            _entries.Add(new Entry(method, path.Split('/'), route.Handle));
        }
    }

    internal sealed record Entry(string Method, string[] Segments, Func<RouteRequest, CancellationToken, Task<RouteResponse>> Handle)
    {
        /// <summary>The route as registered (<c>METHOD /path/{param}</c>), the same for every request it answers.</summary>
        public string Key { get; } = Method + " " + string.Join('/', Segments);
    }

    internal sealed record Match(Entry Entry, IReadOnlyDictionary<string, string> Parameters);

    internal Match? Find(string method, string path)
    {
        foreach (var e in _entries)
        {
            if (e.Method == method && TryMatch(e.Segments, path, out var parameters))
            {
                return new Match(e, parameters);
            }
        }

        return null;
    }

    /// <summary>The methods registered for <paramref name="path"/>, in registration order.</summary>
    internal IReadOnlyList<string> MethodsFor(string path) =>
        [.. _entries.Where(e => TryMatch(e.Segments, path, out _)).Select(static e => e.Method).Distinct()];

    /// <summary>A last segment <c>{*name}</c> takes the rest of the path, slashes included.</summary>
    private static bool IsRest(string segment) => segment.Length > 3 && segment[0] == '{' && segment[1] == '*' && segment[^1] == '}';

    /// <summary>The rest of a path is a slice id: letters, digits and <c>. _ - : / @</c> (the <c>@</c> is for the reserved member <c>@orchestrator</c>), no empty segment and no <c>.</c> or <c>..</c> segment, at most 512 characters.</summary>
    private static bool IsRestText(string text) =>
        text.Length is > 0 and <= 512
        && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/' or '@')
        && text.Split('/').All(static s => s.Length > 0 && s is not ("." or ".."));

    private static bool TryMatch(string[] pattern, string path, out IReadOnlyDictionary<string, string> parameters)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        parameters = found;
        var parts = path.Split('/');
        var rest = pattern.Length > 0 && IsRest(pattern[^1]);
        if (rest ? parts.Length < pattern.Length : parts.Length != pattern.Length)
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var p = pattern[i];
            if (rest && i == pattern.Length - 1)
            {
                var tail = string.Join('/', parts[i..]);
                if (!IsRestText(tail))
                {
                    return false;
                }

                found[p[2..^1]] = tail;
            }
            else if (p.Length > 2 && p[0] == '{' && p[^1] == '}')
            {
                if (parts[i].Length == 0 || !parts[i].All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                {
                    return false;
                }

                found[p[1..^1]] = parts[i];
            }
            else if (!string.Equals(p, parts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
