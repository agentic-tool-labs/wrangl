using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Hooks;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Composition;

/// <summary>What a hook run needs: the hook-error log, the services built on first use, and the reason the state home is refused (null when it is not).</summary>
internal sealed record HookBundle(IHookErrorLog ErrorLog, Func<GateServices> Services, Func<string?>? HomeRefusal = null);

/// <summary>
/// The composition root for native hooks: the service graph (Config, Roster, Hierarchy, the mailbox and links with the state
/// domains each owns, and the state writer every write goes through) plus the peer-mesh gate. Nothing here does I/O until a
/// hook body asks for a service.
/// </summary>
internal static class HookComposition
{
    internal static HookBundle Create(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The environment the caller built the context over, never the process's own: what the launching shell happens to export
        // must not decide a hook's answer in a host that was handed another environment.
        var process = context.Env;
        var env = new DaemonComposition.PluginAwareEnvironment(process, reportsBinaryVersion: false);
        var daemonEnv = new DaemonComposition.PluginAwareEnvironment(process, reportsBinaryVersion: true);
        var io = PlatformModule.CreateIo(null);
        var uid = OperatingSystem.IsWindows() ? (uint?)null : PlatformModule.CurrentUid();
        return new HookBundle(
            io.HookErrors,
            () => Build(io, env, daemonEnv),
            () => AhHomeGate.Check(env, io.Files, uid) is { Accepted: false } refused ? AhHomeGate.InSession(refused) : null);
    }

    private static GateServices Build(PlatformIo io, DaemonComposition.PluginAwareEnvironment env, IAhEnvironment daemonEnv)
    {
        var runner = PlatformModule.CreateProcessRunner();
        var daemon = DaemonComposition.CreateDaemonClient(io, daemonEnv, runner, oneStrike: true);
        var graph = ServiceGraph.Build(io, env, runner, daemon, teamAuthority: Ah.StateDb.StateDbModule.OpenTeamAuthority(env.AhHome, forHook: true), sessionAuthority: Ah.StateDb.StateDbModule.OpenSessionAuthority(env.AhHome, forHook: true), opRunner: Ah.StateDb.StateDbModule.CreateOpRunner(env.AhHome, forHook: true));

        // every service built after the graph reads through the graph's reader, the one that reads the asks logs from the state
        // database (per-dir-files-to-db §6c G5); the raw reader would read their stale files
        io = graph.Io;
        var rule = MeshModule.CreateMeshRule(graph.Links, env);
        var mesh = MeshModule.CreateMeshGate(graph.Mailbox, rule, graph.Links, graph.Hierarchy, io.Clock, graph.Writer, io.Files, graph.RunFacts);
        return new GateServices(
            env,
            io.Clock,
            io.Files,
            graph.Config,
            graph.Roster,
            graph.Hierarchy,
            HierarchyModule.CreateBootstrap(io.Writer),
            graph.Writer,
            io.HookErrors,
            graph.Facts,
            mesh,
            ConfigModule.CreateConventions(runner, io.Files, () => env.HomeDirectory),
            new AdvisoryServices(
                graph.Mailbox,
                MeshModule.CreateMailboxHooks(graph.Mailbox, MeshModule.CreateRelayObligations(graph.Mailbox, graph.Hierarchy), graph.Hierarchy, io.Clock),
                graph.Callers,
                MeshModule.CreateMailCore(io.Clock),
                runner,
                ConfigModule.CreateDirectiveText(graph.Config, ConfigModule.CreateRoleContracts(graph.Config, io.Files, env, io.HookErrors), () => graph.Roster),
                HierarchyModule.CreateContext(io.Files, io.Clock, graph.Roster),
                HierarchyModule.CreateRecovery(io.Files, env, io.Clock),
                daemon,
                new TeamIntent(io.Files, env.Plugin?.Root ?? string.Empty),
                new InstallStampHost(io.Writer, PlatformModule.CreateDetachedSpawner()),
                PlatformModule.CreateTurnEndLog(io.Clock, Ah.Core.Text.AhPaths.TurnEndLog(env.AhHome)),
                graph.Sender),
            new SessionDirectory(graph),
            graph.MemberUp,
            (dir, root, team, names) => Ah.Commands.CommandsModule.ReadyToDismiss(new RosterComposition(graph, io.Clock, env), dir, root, team, names),
            (cwd, session, subject, sha) => Verbs.WorkVerb.RecordCommit(env, graph, cwd, session, subject, sha),
            (cwd, who) => RulesReads.Block(graph, env.AhHome, Verbs.BacklogVerb.DirOf(graph, cwd), new RulesAsk(who.Team, who.Names, who.Orchestrator, who.TeamDir, who.Parent)),
            cwd => Verbs.AdrVerb.Waiting(env.AhHome, HierarchyModule.WorkDirKey(Verbs.BacklogVerb.DirOf(graph, cwd))));
    }
}
