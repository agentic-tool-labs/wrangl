namespace Ah.TestKit;

/// <summary>
/// A directory that no <c>ahprobe.*</c> directory can contain, for the cases that need a path outside every sandbox.
/// <see cref="Directory.CreateTempSubdirectory(string?)"/> follows <c>TMPDIR</c>, and a hermetic run's <c>TMPDIR</c> is a real
/// sandbox: a directory made there is inside one, so the test-mode predicate holds for it and a "not a sandbox" case passes
/// or fails by how the suite was launched.
/// </summary>
public static class NotASandbox
{
    /// <summary>Creates <c>/var/tmp/&lt;prefix&gt;&lt;random&gt;</c> and returns its full path.</summary>
    public static string Create(string prefix)
    {
        const string Root = "/var/tmp";
        if (!Directory.Exists(Root))
        {
            throw new InvalidOperationException($"{Root} does not exist, so no directory can be made outside every sandbox");
        }

        var path = Path.Combine(Root, prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
