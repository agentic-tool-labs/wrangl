using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>Collects the routes modules register through <see cref="IRouteRegistry"/>, beside the <c>[Route]</c> ones.</summary>
internal sealed class RouteRegistry : IRouteRegistry
{
    private readonly List<RouteRegistration> _routes = [];
    private readonly HashSet<string> _patterns;

    private RouteRegistry(IEnumerable<string> taken)
    {
        _patterns = new HashSet<string>(taken, StringComparer.Ordinal);
    }

    /// <summary>The routes registered, in registration order.</summary>
    internal IReadOnlyList<RouteRegistration> Registrations => _routes;

    public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_patterns.Add(pattern))
        {
            throw new ArgumentException($"a route {pattern} is already registered", nameof(pattern));
        }

        _routes.Add(new RouteRegistration(pattern, handler));
    }

    /// <summary>
    /// Runs <paramref name="register"/> once against a registry that already holds the <c>[Route]</c> patterns of
    /// <paramref name="registries"/>, and returns what it registered.
    /// </summary>
    internal static IReadOnlyList<RouteRegistration> Collect(IReadOnlyList<ModuleRegistry> registries, Action<IRouteRegistry>? register)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var collected = new RouteRegistry(registries.SelectMany(static r => r.Routes).Select(static r => r.Pattern));
        register?.Invoke(collected);
        return collected.Registrations;
    }
}
