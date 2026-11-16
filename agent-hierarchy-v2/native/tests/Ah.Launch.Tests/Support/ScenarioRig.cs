using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>A sleeper that moves a fake clock instead of waiting.</summary>
internal sealed class FakeSleeper(FakeClock clock) : ISleeper
{
    internal List<TimeSpan> Sleeps { get; } = [];

    public void Sleep(TimeSpan duration)
    {
        Sleeps.Add(duration);
        clock.UtcNow += duration;
    }
}

internal sealed class RecordingActivity : IActivityRecorder
{
    internal List<(string Dir, string Subject)> Idle { get; } = [];

    internal List<(string Dir, string Subject)> Cleared { get; } = [];

    public void RecordIdle(string dir, string subject) => Idle.Add((dir, subject));

    public void Clear(string dir, string subject) => Cleared.Add((dir, subject));
}

/// <summary>An app-server launcher whose every step answers from a script.</summary>
internal sealed class FakeAppserver : IAppserverLauncher
{
    internal string? Tmp { get; set; } = "/tmp/ok";

    internal int? Pid { get; set; } = 4321;

    internal AppserverBringUp UpResult { get; set; } = new(true, "thread-abc", null);

    internal List<string> Steps { get; } = [];

    internal List<int> Terminated { get; } = [];

    internal List<IReadOnlyList<string>> ServerArgs { get; } = [];

    public string? EnsureMemberTmp(string tmpDirectory)
    {
        Steps.Add("tmp " + tmpDirectory);
        return Tmp;
    }

    public int? Start(string socket, IReadOnlyList<string> serverArgs, string startCwd)
    {
        Steps.Add("start " + socket);
        ServerArgs.Add(serverArgs);
        return Pid;
    }

    public AppserverBringUp BringUp(string socket, JsonObject modes, string cwd)
    {
        Steps.Add("bringup " + socket);
        return UpResult;
    }

    internal List<(int Pid, string Socket)> TerminateRequests { get; } = [];

    /// <summary>The pids whose command line is the app-server of the socket asked about; any other is refused, as a pid that was reused is.</summary>
    internal HashSet<int>? Verified { get; set; }

    public bool Terminate(int pid, string socket)
    {
        TerminateRequests.Add((pid, socket));
        if (Verified is not null && !Verified.Contains(pid))
        {
            return false;
        }

        Terminated.Add(pid);
        return true;
    }
}

/// <summary>The team rows a reconcile would rewrite, held in memory.</summary>
internal sealed class FakeRowStore : ITeamRowStore
{
    internal JsonObject? Fresh { get; set; }

    internal IReadOnlyList<JsonValue>? Written { get; private set; }

    public TeamRowsUpdate UpdateRows(string dir, string path, Func<JsonObject, IReadOnlyList<JsonValue>?> pick, bool keepFile = false, JsonObject? journal = null)
    {
        if (Fresh is null)
        {
            return new TeamRowsUpdate(false, null);
        }

        var rows = pick(Fresh);
        if (rows is null)
        {
            return new TeamRowsUpdate(false, null);
        }

        Written = rows;
        return new TeamRowsUpdate(rows.Count == 0 && !keepFile, rows);
    }
}

/// <summary>
/// A launch core over a scripted multiplexer: the project in the sandbox is a git repo, the clock and the sleeper are fakes, and
/// every process the core starts is a call on <see cref="Mux"/>.
/// </summary>
internal sealed class ScenarioRig
{
    internal required UnitSandbox Sandbox { get; init; }

    internal required string Cwd { get; init; }

    internal required Dictionary<string, string> Variables { get; init; }

    internal required FakeMux Mux { get; init; }

    internal required FakeClock Clock { get; init; }

    internal required FakeSleeper Sleeper { get; init; }

    internal required RecordingActivity Activity { get; init; }

    internal required FakeAppserver Appserver { get; init; }

    internal required MemberLauncher Launcher { get; init; }

    internal required SpawnShapes Shapes { get; init; }

    internal required IConfigCore Config { get; init; }

    internal required IRosterCore Roster { get; init; }

    internal required Herdr Herdr { get; init; }

    internal required MuxRunner MuxRunner { get; init; }

    internal required MuxGuard Guard { get; init; }

    internal required FakeEnvironment Env { get; init; }

    internal required PlatformIo Io { get; init; }

    internal required LaunchContext Context { get; init; }

    internal required HierarchyServices Hierarchy { get; init; }

    internal required IMailbox Mailbox { get; init; }

    internal required IMemberRegistry Registry { get; init; }

    internal required IScreenRules Screens { get; init; }

    internal required IKindHarnesses Harnesses { get; init; }

    internal required ScreenReading Reading { get; init; }

    internal static ScenarioRig Create(Action<Dictionary<string, string>>? env = null, string? teamFile = "alpha", bool parallel = false, Action<string>? report = null, Func<int, bool>? terminate = null)
    {
        var sandbox = UnitSandbox.Create();
        var cwd = sandbox.Root + "/proj";
        Directory.CreateDirectory(cwd + "/.git");
        File.WriteAllText(cwd + "/.git/HEAD", "ref: refs/heads/main\n");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = sandbox.Home,
            ["TZ"] = "UTC",
            ["AH_HOME"] = sandbox.Root + "/ah",
            ["PATH"] = sandbox.Root + "/bin:/usr/bin:/bin",
            ["HERDR_ENV"] = "1",
            ["HERDR_PANE_ID"] = "1-1",
        };
        env?.Invoke(variables);
        var binDir = sandbox.Root + "/bin";
        Directory.CreateDirectory(binDir);
        File.WriteAllText(binDir + "/herdr", "#!/bin/sh\n");
        File.SetUnixFileMode(binDir + "/herdr", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var plugin = new PluginInfo(LaunchRig.AhRoot, "wrangl", "0.0.0", "1");
        var environment = new FakeEnvironment(variables, plugin);
        var io = PlatformModule.CreateIo(plugin);
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        IConfigCore config = null!;
        IRosterCore roster = null!;
        config = ConfigModule.CreateCore(io.Files, environment, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, environment, config, io.Processes);
        var registry = RosterModule.CreateMemberRegistry(config, io.Files, environment);
        var mux = new FakeMux();
        var guard = new MuxGuard(environment, io.Files, io.Appender, 4242, report, terminate);
        var runner = new MuxRunner(mux, guard);
        var herdr = new Herdr(runner, environment, io.Files);
        var screens = RosterModule.CreateScreenRules();
        var harnesses = new KindHarnesses(registry.KindDefault);
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(io.Files, environment, clock, new SequenceRandom(), io.Processes, config, roster, null, new ProcessFacts(cwd, 4242)) { MsgsRoute = TestMsgStore.Route(environment.AhHome) });
        var writer = new DirectDomainWriter();
        var mailbox = MeshModule.CreateMailbox(io.Files, environment, clock, new SequenceRandom(), hierarchy, roster, writer);
        writer.Domain = mailbox.StateDomain();
        var rules = RosterModule.CreateMemberRules(registry);
        var shapes = new SpawnShapes(environment, io.Files, mux, config, roster, rules, registry, harnesses, hierarchy.Messages, new NoTiers());
        var sleeper = new FakeSleeper(clock);
        var activity = new RecordingActivity();
        var appserver = new FakeAppserver();
        var reading = new ScreenReading(herdr, environment, config, screens, harnesses);
        var trust = RosterModule.CreateCodexTrust(io.Files, environment, start => config.FindGitRoot(start));
        var shell = new ShellRunner(mux, guard);
        var launcher = new MemberLauncher(
            environment,
            io.Files,
            io.Writer,
            clock,
            herdr,
            runner,
            shell,
            config,
            screens,
            reading,
            harnesses,
            registry,
            trust,
            shapes,
            new LayoutRules(),
            sleeper,
            activity,
            appserver,
            "node /plugin/hooks/roster.mjs",
            parallel);
        var ctx = new LaunchContext(cwd, teamFile, config.TeamPrefix(cwd, null), config.ResolveConfig(cwd, new ResolveOptions(Team: teamFile)), "spawn-one", teamFile);
        return new ScenarioRig
        {
            Sandbox = sandbox,
            Cwd = cwd,
            Variables = variables,
            Mux = mux,
            Clock = clock,
            Sleeper = sleeper,
            Activity = activity,
            Appserver = appserver,
            Launcher = launcher,
            Shapes = shapes,
            Config = config,
            Roster = roster,
            Herdr = herdr,
            MuxRunner = runner,
            Guard = guard,
            Env = environment,
            Io = io,
            Context = ctx,
            Hierarchy = hierarchy,
            Mailbox = mailbox,
            Registry = registry,
            Screens = screens,
            Harnesses = harnesses,
            Reading = reading,
        };
    }

    /// <summary>A team member row with its launch shape for <paramref name="transport"/>.</summary>
    internal JsonObject Member(string role, string name, string transport = "herdr", string? kind = null, string? model = "sonnet", string route = "peer", JsonObject? extra = null)
    {
        var member = new JsonObject { ["role"] = role, ["name"] = name, ["route"] = route };
        if (kind is not null)
        {
            member["kind"] = kind;
        }

        if (model is not null)
        {
            member["model"] = model;
        }

        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                member[key] = extra[key];
            }
        }

        member["spawn"] = Shapes.SpawnShape(member, transport, Context).Shape;
        return member;
    }
}
