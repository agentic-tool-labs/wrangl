using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>
/// The derive-or-trusted rule for the directories a request claims. The derivation is <see cref="ConfigCore"/>'s own, run with
/// the environment override left out, so the client and the daemon cannot drift.
/// </summary>
internal sealed class HierarchyDirPolicy(ConfigCore core, IFileReader files, IAhEnvironment environment, ITrustStore? trust, Func<uint?> currentUserId) : IHierarchyDirPolicy
{
    private const uint GroupOrOtherWritable = 0x12;

    public string DerivedHierarchyDir(string cwd) => core.DerivedHierarchyDir(cwd);

    public string? DerivedMainHierarchyDir(string cwd) => core.DerivedMainHierarchyDir(cwd);

    public HierarchyOverride Override() => core.OverrideState();

    public RequestContextProblem? CheckClaim(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sandbox = TestSandbox();
        string? acceptedMain = null;
        if (context.MainHierarchyDirectory.Length > 0)
        {
            var claimed = Claimed(context.MainHierarchyDirectory);
            var derivedMain = core.DerivedMainHierarchyDir(context.Cwd);
            var derived = derivedMain is null ? null : Realish(derivedMain);
            if (claimed is null || !(claimed == derived || Trusted(claimed) || Within(sandbox, claimed)))
            {
                return new RequestContextProblem("main_hierarchy_dir is not derived from cwd and not trusted", "main_hierarchy_dir");
            }

            acceptedMain = claimed;
        }

        var hierarchy = Claimed(context.HierarchyDirectory);
        var derivedHierarchy = Realish(core.DerivedHierarchyDir(context.Cwd));
        if (hierarchy is null || !(hierarchy == derivedHierarchy || (acceptedMain is not null && hierarchy == acceptedMain) || Trusted(hierarchy) || Within(sandbox, hierarchy)))
        {
            return new RequestContextProblem("hierarchy_dir is not derived from cwd and not trusted", "hierarchy_dir");
        }

        if (context.SocketDirectory.Length > 0 && !OwnerOnlyDirectory(context.SocketDirectory))
        {
            return new RequestContextProblem("messaging_socket_dir must be a directory owned by the daemon's user and not group- or other-writable", "messaging_socket_dir");
        }

        return null;
    }

    public string? Realish(string path)
    {
        var rest = new List<string>();
        var current = Path.GetFullPath(path);
        while (true)
        {
            // A missing component is climbed past without asking for its real path: that call throws for a missing path, and a
            // removed work tree's directory is resolved on every read that names it (b459: 16% of a start on a large home).
            if (files.Stat(current, followSymlinks: false) is null && Path.GetDirectoryName(current) is { Length: > 0 } parent && parent != current)
            {
                rest.Add(current[(parent.Length == 1 ? 1 : parent.Length + 1)..]);
                current = parent;
                continue;
            }

            try
            {
                var real = files.RealPath(current);
                rest.Reverse();
                return rest.Count == 0 ? real : real.TrimEnd('/') + "/" + string.Join('/', rest);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (files.Stat(current, followSymlinks: false) is { Kind: FileKind.Symlink })
                {
                    return null;
                }

                var up = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(up) || up == current)
                {
                    return Path.GetFullPath(path);
                }

                rest.Add(current[(up.Length == 1 ? 1 : up.Length + 1)..]);
                current = up;
            }
        }
    }

    /// <summary>A claimed path made comparable: absolute, no <c>..</c> or empty segment, then <see cref="Realish"/>; null when it is none of those.</summary>
    private string? Claimed(string path)
    {
        if (!path.StartsWith('/') || path.Split('/').Skip(1).Any(static s => s.Length == 0 || s == ".." || s == "."))
        {
            return path == "/" ? "/" : null;
        }

        return Realish(path);
    }

    private bool Trusted(string realPath) => trust is not null && trust.IsTrusted(TrustFile.HierarchyDirs, realPath);

    private bool OwnerOnlyDirectory(string path)
    {
        if (Claimed(path) is not { } real || currentUserId() is not { } me)
        {
            return false;
        }

        return files.Stat(real) is { Kind: FileKind.Directory } stat && stat.Uid == me && (stat.Mode & GroupOrOtherWritable) == 0;
    }

    /// <summary>The test-mode sandbox ancestor: the <c>ahprobe.*</c> directory <c>AH_HOME</c> lies under; null outside test mode.</summary>
    private string? TestSandbox()
    {
        if (!environment.TestMode || environment.GetVariable("AH_HOME") is not { Length: > 0 } home || Claimed(home) is not { } real)
        {
            return null;
        }

        var dir = real;
        while (dir.Length > 1)
        {
            if (NodePath.Basename(dir).StartsWith("ahprobe.", StringComparison.Ordinal))
            {
                return dir;
            }

            dir = NodePath.Dirname(dir);
        }

        return null;
    }

    private static bool Within(string? ancestor, string path) =>
        ancestor is not null && (path == ancestor || path.StartsWith(ancestor + "/", StringComparison.Ordinal));
}
