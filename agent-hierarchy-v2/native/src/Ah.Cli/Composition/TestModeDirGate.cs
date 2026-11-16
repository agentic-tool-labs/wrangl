using System.Collections.Concurrent;
using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Cli.Composition;

/// <summary>
/// The one rule keeping a test-mode daemon inside its probe root: a hierarchy dir whose real path, or whose team home's real path,
/// lies outside the <c>ahprobe.*</c> ancestor of the real <c>AH_HOME</c> is treated as not listed. Outside test mode it passes every list through.
/// </summary>
internal sealed class TestModeDirGate(IAhEnvironment environment, IFileReader files, Func<string, string> teamHome, Action<string> log)
{
    private readonly ConcurrentDictionary<string, byte> _dropped = new(StringComparer.Ordinal);

    private readonly Lazy<string?> _root = new(() => RootOf(environment, files));

    /// <summary>The <c>ahprobe.*</c> ancestor of the real <c>AH_HOME</c>; null when it has none.</summary>
    internal static string? RootOf(IAhEnvironment environment, IFileReader files) =>
        RealPathResolver.ProbeRootOf(RealPathResolver.ThroughLongestExistingPrefix(files, environment.AhHome));

    /// <summary>Whether the real path of <paramref name="path"/> is <paramref name="root"/> or under it; with no root, nothing is.</summary>
    internal static bool IsInside(IFileReader files, string? root, string? path)
    {
        if (root is null || path is null)
        {
            return false;
        }

        var real = RealPathResolver.ThroughLongestExistingPrefix(files, path);
        return real == root || (real is not null && real.StartsWith(root + "/", StringComparison.Ordinal));
    }

    /// <summary>The distinct dirs dropped so far.</summary>
    public int Dropped => _dropped.Count;

    public IReadOnlyList<string> Admit(IReadOnlyList<string> dirs) =>
        environment.TestMode ? [.. dirs.Where(Kept)] : dirs;

    private bool Kept(string dir)
    {
        if (Inside(dir) && Inside(HomeOf(dir)))
        {
            return true;
        }

        if (_dropped.TryAdd(dir, 0))
        {
            log($"test mode: hierarchy dir outside the probe root dropped: {dir}");
        }

        return false;
    }

    private string? HomeOf(string dir)
    {
        try
        {
            return teamHome(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool Inside(string? path) => IsInside(files, _root.Value, path);
}
