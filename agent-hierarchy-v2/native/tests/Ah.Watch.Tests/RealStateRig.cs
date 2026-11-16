using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.Roster;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>Runs a domain's op as the engine would, in this process: a database body inside the op runner, else the body over the files.</summary>
internal sealed class RealOps(string ahHome) : IStateWriter
{
    internal IReadOnlyList<IStateDomain> Domains { get; set; } = [];

    public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
    {
        var d = Domains.Single(x => x.Name == domain);
        var o = d.Ops.Single(x => x.Name == op);
        var write = o.Kind == OpKind.Write;
        var call = new OpCall(op, argsJson, context, write ? Guid.NewGuid().ToString() : null);
        OpResult result;
        if (write && (domain == "msgs" || d.DbOnly || o.ModeKey is not null) && o.DbBody is { } body)
        {
            var outcome = StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest(o.ModeKey ?? domain, call.OperationId, "direct", tx => body(call, tx), DbOnly: d.DbOnly));
            result = outcome.Status == DbOpStatus.Done ? new OpResult(true, outcome.ResultJson) : new OpResult(false, default, outcome.Error);
        }
        else
        {
            result = o.Body(call, write ? new DirectStoreWriteContext() : null);
        }

        return result.Ok ? new StateResult(true, result.ResultJson) : new StateResult(false, default, result.Error, "op_failed");
    }

    public bool IsHomeScoped(string domain) => Domains.Any(d => d.Name == domain && d.HomeScoped);

    public bool IsDbOnly(string domain) => Domains.Any(d => d.Name == domain && d.DbOnly);

    public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) => throw new NotSupportedException();
}

/// <summary>The view's reads, answered by the real read ops.</summary>
internal sealed class RealReader(RealOps ops, string repo) : IViewStateReader
{
    public int Reads { get; private set; }

    public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
    {
        Reads++;
        var result = ops.Invoke(domain, op, JsonText.ToUtf8(args, 0), new RequestContext(repo, hierarchyDir, string.Empty, string.Empty));
        return result.Ok ? JsonText.Parse(Utf8Text.Decode(result.ResultJson.Span)) : throw new InvalidOperationException(result.Error);
    }
}

/// <summary>One real state store under a sandbox home: the hierarchy's domains and the team domain, with team records as database rows.</summary>
internal sealed record RealStateRig(HierarchyServices Services, RealOps Ops, IRosterCore Roster, string Root, string Repo, string Pool, string AhHome)
{
    public RealReader Reader() => new(Ops, Repo);

    public static RealStateRig Make()
    {
        var sandbox = UnitSandbox.Create();
        var repo = Path.Combine(sandbox.Root, "repo");
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" }, new PluginInfo(sandbox.Root, "wrangl", "0.0.0", "1"));
        using (var open = StateDbModule.Open(env.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var io = PlatformModule.CreateIo(null);
        var facts = new ProcessFacts(repo, 4242);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, facts);
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, teamAuthority: StateDbModule.OpenTeamAuthority(env.AhHome));
        var ops = new RealOps(env.AhHome);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var services = HierarchyModule.Create(new HierarchyEnvironment(
            io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, ops, facts, HierarchyModule.CreateBootstrap(io.Writer))
        {
            MsgsRoute = TestMsgStore.Route(env.AhHome), ProgressLogs = StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints = StateDbModule.CreateCheckpoints(env.AhHome), ProgressDocs = StateDbModule.CreateProgressDocs(env.AhHome),
        });
        ops.Domains = [.. services.StateDomains, RosterModule.CreateStateDomain(roster, clock, new SequenceRandom(), null)];
        return new RealStateRig(services, ops, roster, sandbox.Root, repo, PoolOf(sandbox.Root, "repo"), env.AhHome);
    }

    /// <summary>The hierarchy dir of a further repo <paramref name="name"/> beside the first, created.</summary>
    public string NewPool(string name) => PoolOf(Root, name);

    private static string PoolOf(string root, string name)
    {
        var repo = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        var pool = Path.Combine(repo, ".claude", "ah2");
        Directory.CreateDirectory(pool);
        return pool;
    }

    public void Write(string domain, string op, JsonObject args, string? pool = null)
    {
        var result = Ops.Invoke(domain, op, JsonText.ToUtf8(args, 0), new RequestContext(Repo, pool ?? Pool, string.Empty, string.Empty));
        Assert.True(result.Ok, result.Error);
    }
}
