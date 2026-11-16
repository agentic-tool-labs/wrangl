using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>
/// The request context (<c>cwd</c>, <c>hierarchy_dir</c>, ...) of the route request being served, kept for the writes that
/// request makes below the route handler, which are not handed it. A handler runs inside <see cref="Wrap"/>, and
/// <see cref="Current"/> answers anywhere beneath it, including after an await.
/// </summary>
internal sealed class RequestScope
{
    private readonly AsyncLocal<RequestContext?> _current = new();

    internal RequestContext? Current => _current.Value;

    internal Func<RouteRequest, CancellationToken, Task<RouteResponse>> Wrap(Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) =>
        async (request, cancellation) =>
        {
            _current.Value = From(request);
            try
            {
                return await handler(request, cancellation).ConfigureAwait(false);
            }
            finally
            {
                _current.Value = null;
            }
        };

    internal static RequestContext? From(RouteRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                fields[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        if (!request.Body.IsEmpty && request.Body.Span[0] == (byte)'{' && JsonText.TryParse(request.Body.Span, out var body) && body.IsMap)
        {
            foreach (var key in body.AsObject().Keys)
            {
                if (body.AsObject()[key] is { IsText: true } text)
                {
                    fields[key] = text.AsString();
                }
            }
        }

        if (!fields.TryGetValue("hierarchy_dir", out var hierarchy) || hierarchy.Length == 0)
        {
            return null;
        }

        return new RequestContext(
            fields.GetValueOrDefault("cwd") ?? string.Empty,
            hierarchy,
            fields.GetValueOrDefault("main_hierarchy_dir") ?? string.Empty,
            fields.GetValueOrDefault("messaging_socket_dir") ?? string.Empty);
    }

    /// <summary>A registry that serves each handler inside this scope.</summary>
    internal IRouteRegistry Scoped(IRouteRegistry inner) => new ScopedRegistry(this, inner);

    private sealed class ScopedRegistry(RequestScope scope, IRouteRegistry inner) : IRouteRegistry
    {
        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) =>
            inner.Add(pattern, scope.Wrap(handler));
    }
}
