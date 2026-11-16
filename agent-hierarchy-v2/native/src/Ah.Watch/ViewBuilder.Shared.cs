namespace Ah.Watch;

/// <summary>
/// One model of the hierarchy, read once and shared by every page derived from it: the orchestrators found, each directory's status
/// and each team's facts are read on first use and kept for the life of the model. A page derived from it is the page an unshared
/// build at the same instant would give.
/// </summary>
public interface IViewModel
{
    /// <summary>Whether a page derived so far asked for herdr's reading.</summary>
    bool UsedHerdr { get; }

    /// <summary>The view at <paramref name="path"/>. Pages are derived one at a time; a caller meanwhile waits.</summary>
    /// <param name="path">The view path, as <c>GET /v1/view</c> takes it.</param>
    ViewAnswer Build(string path);
}

internal static partial class ViewBuilder
{
    /// <summary>
    /// Runs one page build inside its own <see cref="Ah.Core.Files.ListingScope"/>, so a file, a stat, a pool listing or a state read
    /// the page repeats is made once, and the content cache is used; what the scope answered is added to the request's timing.
    /// </summary>
    internal static T InPageScope<T>(Func<T> build)
    {
        if (Ah.Core.Files.ListingScope.IsOpen)
        {
            return build();
        }

        using var listings = Ah.Core.Files.ListingScope.Begin();
        try
        {
            return build();
        }
        finally
        {
            ViewTiming.Current?.AddCache(listings.Counts);
        }
    }

    /// <summary>
    /// Opens the listing scope of one page build, for a caller that builds a page itself; the build then runs inside it from its first read.
    /// A scope already open is the build's scope and is left to its owner. Dispose it to end the scope; what it answered is added to the request's timing.
    /// </summary>
    internal static IDisposable OpenBuildScope() =>
        Ah.Core.Files.ListingScope.IsOpen ? new Nothing() : new BuildScope(Ah.Core.Files.ListingScope.Begin());

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class BuildScope(Ah.Core.Files.ListingScope scope) : IDisposable
    {
        public void Dispose()
        {
            ViewTiming.Current?.AddCache(scope.Counts);
            scope.Dispose();
        }
    }

    /// <summary>The <see cref="IViewModel"/> over one <see cref="BuildSession"/>, which is not safe for two threads and so is used under a lock.</summary>
    internal sealed class SharedModel(IViewSource src, double now, IReadOnlyList<string>? extraDirs, Func<HerdrReading>? herdr) : IViewModel
    {
        /// <summary>One session is read at a time across every model, so epochs that follow each other never overlap their whole-hierarchy reads.</summary>
        private static readonly object SessionGate = new();

        private readonly object _gate = new();
        private BuildSession? _session;
        private volatile bool _usedHerdr;

        public bool UsedHerdr => _usedHerdr;

        public ViewAnswer Build(string path)
        {
            lock (_gate)
            {
                if (_session is null)
                {
                    lock (SessionGate)
                    {
                        _session = new BuildSession(src, now, extraDirs, () =>
                        {
                            _usedHerdr = true;
                            return herdr is null ? new HerdrReading(null, HerdrUnreachable) : herdr();
                        });
                    }
                }

                var built = ViewBuilder.InPageScope(() => ViewBuilder.Build(src, path, now, _session));
                return new ViewAnswer(built.Status, built.Body);
            }
        }
    }
}
