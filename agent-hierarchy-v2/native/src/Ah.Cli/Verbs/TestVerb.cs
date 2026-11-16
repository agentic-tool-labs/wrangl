using System.Globalization;
using Ah.Cli.Delegation;
using Ah.Watch;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah test run [--tokens k] [--eta e] [--label t] -- &lt;command…&gt;</c>: runs a command holding CPU tokens of the machine-wide test bucket (the plugin's
/// <c>bin/ah-test-run</c>, one implementation). <c>ah test plan --capacity B [--tokens k] [--cwd d] -- &lt;command…&gt;</c>: what the bucket does to a command,
/// one tab-separated line per fact, for that script: <c>row</c>, <c>tokens</c>, <c>env NAME VALUE</c> and, when the arguments change, <c>arg WORD</c> for every word.
/// </summary>
internal static class TestVerb
{
    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is not ("run" or "plan"))
        {
            stderr.WriteLine("ah test: usage: ah test run [--tokens k] [--eta small|medium|large|seconds] [--label text] -- <command…> | ah test plan --capacity B [--tokens k] -- <command…>");
            return 2;
        }

        return args[0] == "run" ? Exec(context, args[1..], stderr) : Plan(context, args[1..], stdout, stderr);
    }

    private static int Exec(CliContext context, string[] rest, TextWriter stderr)
    {
        var plugin = PluginResolver.Resolve(context.Env, BuildInfo.PluginName, PluginResolver.StampRoot(context.Env, BuildInfo.PluginName), PluginRootOption.Requested);
        if (plugin is null)
        {
            stderr.WriteLine("ah test run: the plugin that ships bin/ah-test-run was not found");
            return 1;
        }

        var script = Path.Combine(plugin.Root, "bin", "ah-test-run");
        if (!File.Exists(script))
        {
            stderr.WriteLine($"ah test run: {script} not found; the plugin install is incomplete");
            return 1;
        }

        context.Exec.Exec([script, .. rest]);
        return 1;
    }

    private static int Plan(CliContext context, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        int? capacity = null;
        int? tokens = null;
        var check = false;
        var cwd = Directory.GetCurrentDirectory();
        var i = 0;
        for (; i < rest.Length && rest[i] != "--"; i++)
        {
            var value = i + 1 < rest.Length ? rest[i + 1] : null;
            switch (rest[i])
            {
                case "--check":
                    check = true;
                    break;
                case "--capacity" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b >= 1:
                    capacity = b;
                    i++;
                    break;
                case "--tokens" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1:
                    tokens = k;
                    i++;
                    break;
                case "--cwd" when value is not null:
                    cwd = value;
                    i++;
                    break;
                default:
                    stderr.WriteLine($"ah test plan: unexpected {rest[i]}");
                    return 2;
            }
        }

        if (i >= rest.Length - 1 || (!check && capacity is null))
        {
            stderr.WriteLine("ah test plan: usage: ah test plan --capacity B [--tokens k] [--cwd dir] -- <command…> | ah test plan --check [--cwd dir] -- <command…>");
            return 2;
        }

        var argv = rest[(i + 1)..];
        if (check)
        {
            return Check(context, cwd, argv, stderr);
        }

        var cap = capacity!.Value;
        var plan = WatchModule.PlanTestRun(context.Env.HomeDirectory, cwd, argv, cap, tokens is { } t ? Math.Min(t, cap) : null, context.Env.GetVariable, dir => context.Services?.Value.Trust.IsTrusted(Ah.Abstractions.TrustFile.HierarchyDirs, dir) == true);
        stdout.Write($"row\t{plan.Row ?? "-"}\n");
        stdout.Write(string.Create(CultureInfo.InvariantCulture, $"tokens\t{plan.Tokens}\n"));
        foreach (var (name, value) in plan.Env.Where(static e => !HasBreak(e.Value)))
        {
            stdout.Write($"env\t{name}\t{value}\n");
        }

        if (plan.Argv is { } changed && !changed.Any(HasBreak))
        {
            foreach (var word in changed)
            {
                stdout.Write($"arg\t{word}\n");
            }
        }

        return 0;
    }

    /// <summary>Exit code for a command form that is wrong (EX_USAGE): a team member's whole-project test run.</summary>
    internal const int WrongForm = 64;

    /// <summary>Runs only the test-scope check: 0 when the command may run, <see cref="WrongForm"/> with the refusal on stderr when not.</summary>
    private static int Check(CliContext context, string cwd, string[] argv, TextWriter stderr)
    {
        var refusal = WatchModule.TestScopeRefusal(context.Env.HomeDirectory, cwd, argv, () => CallerRoleClass(context, cwd));
        if (refusal is null)
        {
            return 0;
        }

        stderr.WriteLine(refusal);
        return WrongForm;
    }

    /// <summary>
    /// The role class of the team member the member marker names (<c>AH_MEMBER_NAME</c>, with its team file in <c>WRANGL_TEAM_FILE</c>); null with
    /// no marker; <see cref="WatchModule.UnknownRoleClass"/> when the marker names a member whose team or role cannot be found.
    /// </summary>
    private static TestCaller? CallerRoleClass(CliContext context, string cwd)
    {
        if (context.Env.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is not { Length: > 0 } member)
        {
            return null;
        }

        if (context.Services?.Value is not { } graph
            || context.Env.GetVariable("WRANGL_TEAM_FILE") is not { Length: > 0 } teamFile || !Path.IsPathRooted(teamFile))
        {
            return WatchModule.TestCallerOf(null);
        }

        var dir = Path.GetDirectoryName(teamFile) ?? string.Empty;
        var home = Path.GetFileName(dir) == "teams" ? Path.GetDirectoryName(dir) ?? dir : dir;
        var env = graph.Roster.EnvTeamFileFor([home], teamFile);
        if (!env.IsMap || env.AsObject().Get("invalid") is not null || env.AsObject().Get("teamName") is not { IsText: true } team)
        {            return WatchModule.TestCallerOf(null);
        }

        var homes = env.AsObject().Get("home") is { IsText: true } h ? new[] { home, h.AsString() } : [home];
        var resolved = graph.Config.ResolveConfig(cwd, new Ah.Config.ResolveOptions());
        // The team row (the store once it holds the teams, else the file) names the role; the grants record is the fallback.
        var teamRole = graph.Roster.ReadTeamFileAt(teamFile).Member("members") is { IsArray: true } rows
            ? rows.AsArray().Where(r => r.IsMap && r.Member("name") is { IsText: true } n && n.AsString() == member)
                .Select(r => r.Member("role") is { IsText: true } role ? role.AsString() : null).FirstOrDefault()
            : null;
        return WatchModule.TestCallerOf(Ah.Config.ConfigModule.MemberRole(Ah.Config.ConfigModule.CreateWorktreeGrants(graph.Io.Files, context.Env), homes, team.AsString(), member, resolved, teamRole));
    }

    private static bool HasBreak(string s) => s.Contains('\n', StringComparison.Ordinal) || s.Contains('\t', StringComparison.Ordinal);
}
