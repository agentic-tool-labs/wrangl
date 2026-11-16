using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.State;

/// <summary>A request that fails a check: <paramref name="Field"/> names the offending field when there is one.</summary>
internal sealed record RequestProblem(string Message, string? Field);

/// <summary>
/// The checks a state request goes through before its op runs: the caller's directories must be real, absolute and (for
/// the ones the daemon writes under) owned by the daemon's user, the hierarchy directories it claims must be the ones its
/// <c>cwd</c> derives to (or ones the user trusts), and an op's path arguments must lie inside them. Nothing here comes from
/// the process's own environment.
/// </summary>
internal sealed class StateRequestChecks(IFileReader files, Func<uint?> currentUserId, IHierarchyDirPolicy policy)
{
    private static readonly string[] ContextFields = ["cwd", "hierarchy_dir", "main_hierarchy_dir", "messaging_socket_dir"];

    /// <summary>
    /// The first failed check of the context in <paramref name="context"/> (an object), or null. For a
    /// <paramref name="homeScoped"/> domain the hierarchy directories are only checked for form (an absolute path when
    /// present): they need not exist, be owned by the daemon's user, or match the claim, because no op reads them. For a
    /// <paramref name="dbOnly"/> domain a hierarchy directory that is a verified linked work tree's own and does not exist is accepted,
    /// because no op writes under it.
    /// </summary>
    internal RequestProblem? CheckContext(JsonValue context, bool homeScoped = false, bool dbOnly = false)
    {
        var uid = currentUserId();
        foreach (var field in ContextFields)
        {
            var value = context.Member(field);
            var optional = field is "main_hierarchy_dir" or "messaging_socket_dir";
            if (optional && (value is null || value.Value.IsNull))
            {
                continue;
            }

            if (homeScoped && field is "hierarchy_dir" or "main_hierarchy_dir")
            {
                if (value is not { IsText: true } text || !text.AsString().StartsWith('/'))
                {
                    return new RequestProblem($"{field} must be an absolute path", field);
                }

                continue;
            }

            if (dbOnly && field is "hierarchy_dir" or "main_hierarchy_dir" && value is { IsText: true } dir && MissingLinkedTreeDir(dir.AsString(), uid))
            {
                continue;
            }

            if (CheckDir(field, value, owned: field != "cwd", uid) is { } problem)
            {
                return problem;
            }
        }

        return homeScoped ? null : policy.CheckClaim(Context(context)) is { } claim ? new RequestProblem(claim.Message, claim.Field) : null;
    }

    /// <summary>The first problem with the hierarchy directory of an in-process read: it must be an absolute path with no empty, <c>.</c> or <c>..</c> segment.</summary>
    internal static RequestProblem? CheckInternalDir(string hierarchyDir) =>
        hierarchyDir.StartsWith('/') && hierarchyDir.Split('/').Skip(1).All(static s => s.Length > 0 && s != "." && s != "..")
            ? null
            : new RequestProblem("hierarchy_dir must be an absolute path with no . or .. segment", "hierarchy_dir");

    /// <summary>The request context of a context object that <see cref="CheckContext"/> accepted.</summary>
    internal static RequestContext Context(JsonValue context) => new(
        Text(context, "cwd"),
        Text(context, "hierarchy_dir"),
        Text(context, "main_hierarchy_dir"),
        Text(context, "messaging_socket_dir"));

    /// <summary>
    /// The first of <paramref name="names"/> (an op's declared path arguments) in <paramref name="args"/> that is not an
    /// absolute path inside the request's <c>hierarchy_dir</c> or <c>main_hierarchy_dir</c> (a worktree's team file lives
    /// in the main checkout's), or null.
    /// </summary>
    internal RequestProblem? CheckPathArgs(IReadOnlyList<string> names, JsonValue args, RequestContext context)
    {
        if (names.Count == 0)
        {
            return null;
        }

        // One resolution per distinct path: an in-process read names the same directory as its hierarchy directory and its dir argument, and each
        // resolution walks the path with a syscall per component.
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Realish(string path)
        {
            if (!resolved.TryGetValue(path, out var real))
            {
                real = policy.Realish(path);
                resolved[path] = real;
            }

            return real;
        }

        var dirs = new[] { context.HierarchyDirectory, context.MainHierarchyDirectory }
            .Where(static d => d.Length > 0)
            .Select(Realish)
            .OfType<string>()
            .ToArray();
        foreach (var name in names)
        {
            if (args.Member(name) is not { } value)
            {
                continue;
            }

            if (!value.IsText || !value.AsString().StartsWith('/'))
            {
                return new RequestProblem($"{name} must be an absolute path", name);
            }

            if (value.AsString().Split('/').Contains(".."))
            {
                return new RequestProblem($"{name} must not contain a .. segment", name);
            }

            var path = Realish(value.AsString());
            if (path is null || !dirs.Any(d => path == d || path.StartsWith(d + "/", StringComparison.Ordinal)))
            {
                return new RequestProblem($"{name} {value.AsString()} is outside the request's hierarchy directories", name);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> does not exist and is the hierarchy directory its tree root derives to, a root that exists and
    /// whose <c>.git</c> pointer resolves to a main checkout, owned (like every directory the daemon writes under) by the daemon's user.
    /// </summary>
    private bool MissingLinkedTreeDir(string path, uint? uid) =>
        path.StartsWith('/')
        && files.Stat(path) is null
        && Ah.Core.Text.AhPaths.RepoRootOf(path) is { } root
        && files.Stat(root) is { Kind: FileKind.Directory } rootStat
        && (uid is not { } me || rootStat.Uid == me)
        && policy.DerivedMainHierarchyDir(root) is not null
        && policy.DerivedHierarchyDir(root) == path;

    private RequestProblem? CheckDir(string field, JsonValue? value, bool owned, uint? uid)
    {
        if (value is not { IsText: true } text || !text.AsString().StartsWith('/'))
        {
            return new RequestProblem($"{field} must be an absolute path", field);
        }

        var path = text.AsString();
        var stat = files.Stat(path);
        if (stat is null)
        {
            return new RequestProblem($"{field} {path} does not exist", field);
        }

        if (stat.Value.Kind != FileKind.Directory)
        {
            return new RequestProblem($"{field} {path} is not a directory", field);
        }

        if (owned && uid is { } me && stat.Value.Uid != me)
        {
            return new RequestProblem($"{field} {path} is not owned by the daemon's user", field);
        }

        return null;
    }

    private static string Text(JsonValue context, string field) =>
        context.Member(field) is { IsText: true } v ? v.AsString() : string.Empty;
}
