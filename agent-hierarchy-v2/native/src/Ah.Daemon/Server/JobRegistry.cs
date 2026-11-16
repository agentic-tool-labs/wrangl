using Ah.Abstractions;

namespace Ah.Daemon;

/// <summary>Collects the jobs modules register through <see cref="IJobRegistry"/>, beside the <c>[Job]</c> ones.</summary>
internal sealed class JobRegistry : IJobRegistry
{
    /// <summary>The daemon's own fence and idle job; no module may take its name.</summary>
    internal const string FenceName = "fence-idle";

    private readonly List<JobRegistration> _jobs = [];
    private readonly HashSet<string> _names;

    private JobRegistry(IEnumerable<string> taken)
    {
        _names = new HashSet<string>(taken, StringComparer.Ordinal) { FenceName };
    }

    /// <summary>The jobs registered, in registration order.</summary>
    internal IReadOnlyList<JobRegistration> Registrations => _jobs;

    public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "a job's period must be above zero");
        }

        if (!_names.Add(name))
        {
            throw new ArgumentException($"a job named {name} is already registered", nameof(name));
        }

        _jobs.Add(new JobRegistration(name, period, handler));
    }

    public void Detail(string name, Func<string?> detail)
    {
        var at = _jobs.FindIndex(j => j.Name == name);
        if (at < 0)
        {
            throw new ArgumentException($"no job named {name} is registered", nameof(name));
        }

        _jobs[at] = _jobs[at] with { Detail = detail };
    }

    public void Meter(string name, Func<long?> takeCpuNs)
    {
        var at = _jobs.FindIndex(j => j.Name == name);
        if (at < 0)
        {
            throw new ArgumentException($"no job named {name} is registered", nameof(name));
        }

        _jobs[at] = _jobs[at] with { TakeCpuNs = takeCpuNs };
    }

    /// <summary>
    /// Runs <paramref name="register"/> once against a registry that already holds the <c>[Job]</c> names of
    /// <paramref name="registries"/>, and returns what it registered.
    /// </summary>
    internal static IReadOnlyList<JobRegistration> Collect(IReadOnlyList<ModuleRegistry> registries, Action<IJobRegistry>? register)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var collected = new JobRegistry(registries.SelectMany(static r => r.Jobs).Select(static j => j.Name));
        register?.Invoke(collected);
        return collected.Registrations;
    }
}
