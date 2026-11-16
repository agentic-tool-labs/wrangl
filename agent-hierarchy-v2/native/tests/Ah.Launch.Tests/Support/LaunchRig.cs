using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Launch.Tests;

/// <summary>The declared model tiers: none, as in a home with no <c>modelTiers</c>.</summary>
internal sealed class NoTiers : IModelTiers
{
    public IReadOnlyList<string> DeclaredModelsAt(string kind, IReadOnlyList<string> tiers) => [];
}

/// <summary>A state writer that runs a domain's op bodies directly, as the mailbox needs for its writes.</summary>
internal sealed class DirectDomainWriter : IStateWriter
{
    internal IStateDomain? Domain { get; set; }

    public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
    {
        var spec = Domain!.Ops.Single(o => o.Name == op);
        var outcome = spec.Body(new OpCall(op, argsJson, context), new DirectStoreWriteContext());
        return outcome.Ok
            ? new StateResult(true, outcome.ResultJson)
            : new StateResult(false, ReadOnlyMemory<byte>.Empty, outcome.Error, "op_failed");
    }

    public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
        throw new NotSupportedException();
}

/// <summary>
/// The services one table case runs against, wired over the real file system of the case's sandbox and a real process runner: the
/// same way a composition root wires Ah.Launch, so a case exercises the code a command would.
/// </summary>
internal sealed class LaunchRig
{
    private static readonly string NativeRoot = typeof(LaunchRig).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static readonly Regex StrippedNames = new("^(AH_|AHD_|AGENT_HIERARCHY|HERDR|HC_|TMUX|CLAUDE|TZ$)", RegexOptions.CultureInvariant);

    internal static string AhRoot => Path.GetFullPath(Path.Combine(NativeRoot, ".."));

    internal required UnitSandbox Sandbox { get; init; }

    internal required Dictionary<string, string> Variables { get; init; }

    internal required FakeEnvironment Env { get; init; }

    internal required PlatformIo Io { get; init; }

    internal required TestProcessRunner Runner { get; init; }

    internal required FakeClock Clock { get; init; }

    internal required MuxGuard Guard { get; init; }

    internal required MuxRunner Mux { get; init; }

    internal required Herdr Herdr { get; init; }

    internal required IConfigCore Config { get; init; }

    internal required IRosterCore Roster { get; init; }

    internal required IMemberRegistry Registry { get; init; }

    internal required IMemberRules Rules { get; init; }

    internal required IScreenRules Screens { get; init; }

    internal required IKindHarnesses Harnesses { get; init; }

    internal required ScreenReading Reading { get; init; }

    internal required HierarchyServices Hierarchy { get; init; }

    internal required IMailbox Mailbox { get; init; }

    internal required string Cwd { get; init; }

    /// <summary>The rig for <paramref name="c"/>: the case's sandbox, its environment over a filtered copy of this process's, its clock.</summary>
    internal static LaunchRig Create(UnitSandbox sandbox, UnitCase c, Action<string>? report = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!StrippedNames.IsMatch(key))
            {
                variables[key] = (string)(entry.Value ?? string.Empty);
            }
        }

        variables["HOME"] = sandbox.Home;
        variables["TZ"] = "UTC";
        variables["AH_TEST"] = "1";
        variables["PATH"] = "/usr/bin:/bin";
        if (c.Env is not null)
        {
            foreach (var k in c.Env.Keys)
            {
                if (c.Env[k].IsText)
                {
                    variables[k] = sandbox.ReplaceRoot(c.Env[k].AsString());
                }
            }
        }

        var plugin = new PluginInfo(AhRoot, "wrangl", "0.0.0", "1");
        var env = new FakeEnvironment(variables, plugin);
        var io = PlatformModule.CreateIo(plugin);
        var cwd = sandbox.WorkingDirectory(c);
        var runner = new TestProcessRunner(variables);
        var clock = new FakeClock(c.Now is not null ? DateTimeOffset.Parse(c.Now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow) { Offset = TimeSpan.Zero };

        IConfigCore config = null!;
        IRosterCore roster = null!;
        config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var registry = RosterModule.CreateMemberRegistry(config, io.Files, env);
        var guard = new MuxGuard(env, io.Files, io.Appender, System.Environment.ProcessId, report);
        var mux = new MuxRunner(runner, guard);
        var herdr = new Herdr(mux, env, io.Files);
        var screens = RosterModule.CreateScreenRules();
        var harnesses = new KindHarnesses(registry.KindDefault);
        // a planted peers.jsonl is taken into roster_records by the import pass (files-off-disk §3), once the store exists
        var planted = Directory.EnumerateFiles(sandbox.Root, "peers.jsonl", SearchOption.AllDirectories).ToList();
        if (planted.Count > 0)
        {
            using (Ah.StateDb.StateDbModule.Open(env.AhHome, mayCreate: true))
            {
            }

            foreach (var file in planted)
            {
                _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(env.AhHome, Path.GetDirectoryName(file)!);
            }
        }

        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, null, new ProcessFacts(cwd, 4242)) { MsgsRoute = TestMsgStore.Route(env.AhHome), RosterRecords = Ah.StateDb.StateDbModule.CreateRosterRecords(env.AhHome) });
        var writer = new DirectDomainWriter();
        var mailbox = MeshModule.CreateMailbox(io.Files, env, clock, new SequenceRandom(), hierarchy, roster, writer);
        writer.Domain = mailbox.StateDomain();
        return new LaunchRig
        {
            Sandbox = sandbox,
            Variables = variables,
            Env = env,
            Io = io,
            Runner = runner,
            Clock = clock,
            Guard = guard,
            Mux = mux,
            Herdr = herdr,
            Config = config,
            Roster = roster,
            Registry = registry,
            Rules = RosterModule.CreateMemberRules(registry),
            Screens = screens,
            Harnesses = harnesses,
            Reading = new ScreenReading(herdr, env, config, screens, harnesses),
            Hierarchy = hierarchy,
            Mailbox = mailbox,
            Cwd = cwd,
        };
    }
}
