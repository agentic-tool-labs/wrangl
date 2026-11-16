using System.Text.Json;
using Ah.Abstractions;

namespace Ah.TestKit;

/// <summary>
/// The Node state registry's op table (the <c>DOMAINS</c> of the Node <c>lib-state</c>, recorded before the Node files were
/// removed in <c>tests/contracts/recorded/node-state-registry.json</c>): for each domain and op, its kind, style, daemon flag, lock
/// optionality and the names of its path arguments. A native domain's ops must declare the same path arguments, because the
/// daemon's route refuses a path outside the request's hierarchy directories only for the arguments an op declares.
/// </summary>
public static class NodeStateRegistry
{
    private const string Recording = "tests/contracts/recorded/node-state-registry.json";

    /// <summary>The plugin root that holds the recorded registry, found by walking up from the test's directory; null when absent.</summary>
    public static string? FindPluginRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, Recording)))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>The recording as a document: domain, then op, then <c>kind</c>, <c>style</c>, <c>daemon</c>, <c>lockOptional</c>, <c>pathArgs</c>.</summary>
    public static JsonDocument LoadMetadata(string pluginRoot) => JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginRoot, Recording)));

    /// <summary>Domain name to op name to the op's declared path arguments, as the Node registry had them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Load(string pluginRoot)
    {
        using var document = LoadMetadata(pluginRoot);
        var domains = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var domain in document.RootElement.EnumerateObject())
        {
            var ops = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var op in domain.Value.EnumerateObject())
            {
                ops[op.Name] = [.. op.Value.GetProperty("pathArgs").EnumerateArray().Select(static a => a.GetString()!)];
            }

            domains[domain.Name] = ops;
        }

        return domains;
    }
}

/// <summary>The conformance check a native <see cref="IStateDomain"/> runs against the Node registry.</summary>
public static class StatePathArgsConformance
{
    /// <summary>
    /// Ops this build added after the Node registry was recorded, as <c>domain.op</c>, with the path arguments each must declare
    /// (the daemon refuses a path outside the request's hierarchy directories only for the arguments an op declares).
    /// </summary>
    private static readonly Dictionary<string, string[]> NativeOnlyOps = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["mailbox.claim"] = [],
        ["mailbox.note"] = [],
        ["mailbox.registrations"] = [],
        ["mailbox.entries"] = [],
        ["mailbox.stamp"] = [],
        ["mailbox.counts"] = [],
        ["links.lapse"] = [],
        ["links.round"] = ["dir"],
        ["team.names"] = ["dir"],
        ["msgs.prune"] = ["dir"],
        ["session-pids.pids"] = [],
        ["session-pids.of-pid"] = [],
        ["activity.read"] = [],
        ["mailbox.registration"] = [],
        ["mailbox.keys"] = [],
        ["team.keys"] = ["dir"],
        ["decisions.keys"] = ["dir"],
        ["worktree-grants.keys"] = ["dir"],
        ["activity.keys"] = ["dir"],
        ["jobs.put"] = ["dir", "path"],
        ["jobs.prune"] = ["dir", "cutoff_ms"],
        ["status.read"] = [],
        ["status.stamp"] = [],
        ["msgs.keys"] = ["dir"],
        ["msgs.read"] = ["dir"],
        ["peers.latest"] = ["dir"],
        ["msgs.heard"] = ["dir"],
        ["progress.keys"] = ["dir"],
        ["progress.events"] = ["dir"],
        ["progress.checklists"] = ["dir"],
        ["progress.streams"] = ["dir"],
        ["known-dirs.read"] = [],
        ["msgs.open-exchanges"] = [],
        ["dispatches.open"] = [],
        ["queue.read"] = [],
        ["status.compute"] = [],
        ["msgs.trees"] = [],
        ["msgs.version"] = [],
        ["msgs.changed"] = [],
        ["decisions.pipeline-run"] = [],
        ["decisions.ask"] = ["path"],
        ["progress.read"] = [],
        ["progress.plan"] = [],
        ["progress.describe"] = [],
        ["progress.ledger-append"] = ["dir"],
        ["progress.ledger-cursor"] = ["dir"],
        ["progress.release-seen"] = ["dir"],
        ["peer-records.compact"] = [],
        ["qa.append"] = [],
        ["qa.compact"] = [],
        ["qa.read"] = [],
        ["msgs.retire"] = ["path", "dir"],
        ["msgs.seal"] = ["path", "dir"],
    };

    /// <summary>Whether <c>domain.op</c> is an op this build added after the Node registry was recorded.</summary>
    /// <param name="domain">The domain name.</param>
    /// <param name="op">The op name.</param>
    /// <returns>True for an op with no Node registry row.</returns>
    public static bool IsNativeOnly(string domain, string op) => NativeOnlyOps.ContainsKey($"{domain}.{op}");

    /// <summary>
    /// What is wrong with <paramref name="domain"/>'s declared path arguments, one line each; empty when every op declares
    /// exactly the arguments the Node registry lists for the same domain and op, and no op is unknown to the registry.
    /// </summary>
    public static IReadOnlyList<string> Violations(IStateDomain domain, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> registry)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(registry);
        var found = new List<string>();
        if (!registry.TryGetValue(domain.Name, out var ops))
        {
            // A domain added after the Node registry was recorded is accepted only when every op of it is listed as native-only.
            if (domain.Ops.Count == 0 || !domain.Ops.All(o => IsNativeOnly(domain.Name, o.Name)))
            {
                return [$"{domain.Name}: not a domain of the Node registry"];
            }

            ops = new Dictionary<string, IReadOnlyList<string>>();
        }

        foreach (var op in domain.Ops)
        {
            if (!ops.TryGetValue(op.Name, out var expected))
            {
                // An op added after the Node registry was recorded has no row to match; it must declare the arguments listed for it here.
                if (NativeOnlyOps.TryGetValue($"{domain.Name}.{op.Name}", out var nativeArgs)
                    && (op.PathArgs ?? []).Order(StringComparer.Ordinal).SequenceEqual(nativeArgs.Order(StringComparer.Ordinal)))
                {
                    continue;
                }

                found.Add($"{domain.Name}.{op.Name}: not an op of the Node registry");
                continue;
            }

            var declared = op.PathArgs ?? [];
            var missing = expected.Except(declared, StringComparer.Ordinal).ToList();
            var extra = declared.Except(expected, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                found.Add($"{domain.Name}.{op.Name}: path argument(s) not declared: {string.Join(", ", missing)}");
            }

            if (extra.Count > 0)
            {
                found.Add($"{domain.Name}.{op.Name}: declared but not a path argument in the Node registry: {string.Join(", ", extra)}");
            }
        }

        return found;
    }
}
