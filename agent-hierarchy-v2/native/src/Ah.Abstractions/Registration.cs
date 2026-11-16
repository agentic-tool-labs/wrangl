namespace Ah.Abstractions;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class VerbAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class HookAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RouteAttribute(string pattern) : Attribute
{
    /// <summary>An HTTP method and path, for example <c>POST /send</c>.</summary>
    public string Pattern { get; } = pattern;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class JobAttribute(string name, int periodMilliseconds) : Attribute
{
    public string Name { get; } = name;

    public int PeriodMilliseconds { get; } = periodMilliseconds;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StateDomainAttribute(string name, params string[] stores) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The canonical store names this domain writes. A store name belongs to exactly one assembly.</summary>
    public IReadOnlyList<string> Stores { get; } = stores;
}

/// <summary>A verb's entry point <c>Run(rest, stdout, stderr)</c>: arguments after the verb name, stdout, stderr; returns the exit code.</summary>
public sealed record VerbRegistration(string Name, Func<string[], TextWriter, TextWriter, int> Run);

public sealed record HookRegistration(string Name, Func<HookInvocation, HookOutcome> Run);

public sealed record RouteRegistration(string Pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> Handle);

public sealed record JobRegistration(string Name, TimeSpan Period, Func<CancellationToken, Task> Run)
{
    /// <summary>Extra tokens for the job's entry in the daemon's per-minute jobs line, taken (and reset) once per line; null or empty adds none.</summary>
    public Func<string?>? Detail { get; init; }

    /// <summary>
    /// Reads and resets the thread CPU nanoseconds the job's own body used since the last read (null: unknown). When set, the jobs line
    /// reports it as the job's <c>cpu=</c> instead of timing the daemon thread, which a body that moves threads would misstate.
    /// </summary>
    public Func<long?>? TakeCpuNs { get; init; }
}

public sealed record StateDomainRegistration(string Name, IReadOnlyList<string> Stores, string Target);

/// <summary>
/// Where a module registers a periodic job that needs its collaborators. A module exposes
/// <c>Register(IJobRegistry, &lt;Module&gt;Dependencies)</c>, which the composition root calls once; a <c>[Job]</c> method is for a job that
/// needs none. The daemon runs every job on its loop, one at a time per job, and never starts a job while it drains.
/// </summary>
public interface IJobRegistry
{
    /// <summary>Adds a job. Throws when <paramref name="name"/> is empty or already taken, or <paramref name="period"/> is not above zero.</summary>
    void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler);

    /// <summary>Sets the extra tokens the jobs line carries for the already-registered job <paramref name="name"/>; a registry that keeps no jobs line ignores it.</summary>
    void Detail(string name, Func<string?> detail)
    {
    }

    /// <summary>Sets the CPU meter (<see cref="JobRegistration.TakeCpuNs"/>) of the already-registered job <paramref name="name"/>; a registry that keeps no jobs line ignores it.</summary>
    void Meter(string name, Func<long?> takeCpuNs)
    {
    }
}

/// <summary>
/// Where a module registers a route whose handler needs its collaborators (a <c>[Route]</c> method is for one that needs none).
/// The composition root calls the module's registration once; a pattern is an HTTP method and path, for example
/// <c>GET /v1/view</c>.
/// </summary>
public interface IRouteRegistry
{
    /// <summary>Adds a route. Throws when <paramref name="pattern"/> is empty or already taken.</summary>
    void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler);
}

/// <summary>What one assembly registers, produced at compile time by Ah.Generators.</summary>
public sealed record ModuleRegistry(
    IReadOnlyList<VerbRegistration> Verbs,
    IReadOnlyList<HookRegistration> Hooks,
    IReadOnlyList<RouteRegistration> Routes,
    IReadOnlyList<JobRegistration> Jobs,
    IReadOnlyList<StateDomainRegistration> StateDomains);
