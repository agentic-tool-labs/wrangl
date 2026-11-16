using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// One scripted conversation of join calls over a sandboxed team, replayed against the recorded Node run: every call's result or
/// refusal, the mailbox entries it sent and the files it created must match, with ids, roots and path hashes normalised.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class JoinCommandsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\b\d{8}-\d{6}-[0-9a-z]+\b")]
    private static partial Regex IdRe();

    [GeneratedRegex(@"\bjoin-[0-9a-z]{4}\b")]
    private static partial Regex SlugRe();

    [GeneratedRegex(@"~[0-9a-f]{10}\b")]
    private static partial Regex HashRe();

    private static readonly JsonObject Goldens =
        JsonText.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "join-goldens.json"))).AsObject();

    private sealed class Probe(bool alive, string? command) : IProcessInspector
    {
        public bool PidAlive(int pid) => alive;

        public bool PidOwned(int pid) => alive;

        public string? CommandLine(int pid) => command;
    }

    private sealed class Recorder(string teamFile) : IJoinEffects, ILinkMirror
    {
        internal List<JsonObject> Notices { get; } = [];

        internal List<JsonObject> Created { get; } = [];

        internal List<bool> TargetContexts { get; } = [];

        internal string? LaneRefusal { get; set; }

        public string? LaneMembersRefusal(string dir, string team) => LaneRefusal;

        public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation) =>
            throw new NotSupportedException("a join makes no links operation");

        public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false)
        {
            TargetContexts.Add(ownPoolContext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
            catch (IOException) when (File.Exists(path))
            {
                return Task.FromResult(false);
            }

            Created.Add(new JsonObject { ["kind"] = "create", ["path"] = path, ["content"] = content, ["opId"] = opId });
            return Task.FromResult(true);
        }

        public Task SettleMessageAsync(string dir, string path, string content, string from, string opId, CancellationToken cancellation)
        {
            File.WriteAllText(path, content);
            Created.Add(new JsonObject { ["kind"] = "settle", ["path"] = path, ["content"] = content, ["from"] = from, ["opId"] = opId });
            return Task.CompletedTask;
        }

        public Task AddMemberAsync(string dir, string team, JsonObject row, CancellationToken cancellation)
        {
            var data = JsonText.Parse(File.ReadAllText(teamFile)).AsObject();
            data["members"].AsArray().Add(row);
            File.WriteAllText(teamFile, JsonText.Stringify(data, 0));
            Created.Add(new JsonObject { ["kind"] = "member", ["team"] = team, ["row"] = row });
            return Task.CompletedTask;
        }

        public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation)
        {
            var row = new JsonObject { ["to"] = notice.To, ["from"] = notice.From, ["kind"] = notice.Kind };
            if (notice.Event is not null)
            {
                row["event"] = notice.Event;
            }

            row["text"] = notice.Text;
            Notices.Add(row);
            return Task.CompletedTask;
        }

        public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation) => Task.CompletedTask;
    }

    private sealed class Rig
    {
        private readonly string _root;
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _names = [];

        internal Rig(IReadOnlyDictionary<string, string>? extraVars = null)
        {
            var sandbox = UnitSandbox.Create();
            _root = sandbox.Root;
            Project = _root + "/proj";
            Hierarchy = Project + "/.claude/ah2";
            Directory.CreateDirectory(Hierarchy);
            var vars = new Dictionary<string, string>
            {
                ["HOME"] = sandbox.Home,
                ["AH_HOME"] = _root + "/ah",
                ["AH_TEST"] = "1",
                ["AHD_JOIN_MS"] = "{\"expiryMs\":600000,\"cooldownMs\":600000,\"rowWaitMs\":60000}",
            };
            foreach (var (k, v) in extraVars ?? new Dictionary<string, string>())
            {
                vars[k] = v;
            }

            Fx = MeshFixture.Create(vars, T0, _root);
            TeamFile = Fx.Roster.TeamPath(Hierarchy, "tm");
            Directory.CreateDirectory(Path.GetDirectoryName(TeamFile)!);
            var team = Goldens["team"].AsObject().Clone();
            team["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId };
            File.WriteAllText(TeamFile, JsonText.Stringify(team, 0));
            Context = new RequestContext(Project, Hierarchy, string.Empty, string.Empty);
            Recorder = new Recorder(TeamFile);
            Config = Ah.Config.ConfigModule.CreateCore(Fx.Files, Fx.Environment, () => Fx.Roster, new Ah.Config.ProcessFacts(_root, 4242));
            Lookups = MeshModule.CreateContractLookups(Fx.Mailbox, Fx.Hierarchy.Core, Config);
        }

        internal MeshFixture Fx { get; }

        internal string Project { get; }

        internal string Hierarchy { get; }

        internal string TeamFile { get; }

        internal RequestContext Context { get; }

        internal Recorder Recorder { get; }

        internal Ah.Config.IConfigCore Config { get; }

        internal IContractLookups Lookups { get; }

        internal IJoinCommands Joins(IProcessInspector probe) =>
            MeshModule.CreateJoinCommands(
                Fx.Rule, Fx.Links, Fx.Hierarchy.Core, Fx.Hierarchy.Messages, Fx.Files, Fx.Clock, Fx.Mailbox, Lookups, Config, Fx.Environment, probe, Recorder, Recorder);

        internal string Ref(string text) =>
            Regex.Replace(text, @"<join (\d+)>", m => _names[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1]);

        internal void Opened(string id) => _names.Add(id);

        internal bool Known(string id) => _names.Contains(id);

        /// <summary>The record text with this run's root, plugin root, path hashes, slugs and ids replaced by the stable names the golden uses.</summary>
        internal JsonValue Normalise(JsonValue value)
        {
            var text = JsonText.Stringify(value, 0)
                .Replace(_root, "<ROOT>", StringComparison.Ordinal)
                .Replace(MeshFixture.PluginRoot, "<PLUGIN>", StringComparison.Ordinal);
            text = Ah.TestKit.RecordedCliForm.Map(text);
            text = SlugRe().Replace(text, "join-<S>");
            text = HashRe().Replace(text, "~<H>");
            text = IdRe().Replace(text, m =>
            {
                if (!_ids.TryGetValue(m.Value, out var name))
                {
                    name = $"<ID{(_ids.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}>";
                    _ids[m.Value] = name;
                }

                return name;
            });
            return JsonText.Parse(text);
        }
    }

    [Fact]
    public void TheDecisionCommandsRefuseAnIdAShellCouldRead()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));

        var ok = joins.DecisionCommands("20260301-120000-abcd");

        Assert.Equal("ah msg join accept 20260301-120000-abcd --basis user-approved --why '<edit: why you approve>'", ok["accept"]);
        Assert.Equal("ah msg join deny 20260301-120000-abcd --basis user-declined --why '<edit: why you decline>'", ok["deny"]);
        Assert.Throws<ArgumentException>(() => joins.DecisionCommands("-rf"));
        Assert.Throws<ArgumentException>(() => joins.DecisionCommands("a b"));
        Assert.Throws<ArgumentException>(() => joins.DecisionCommands("a;b"));
        Assert.Throws<ArgumentException>(() => joins.DecisionCommands("a\n"));
    }

    [Fact]
    public async Task OnlyUndecidedUnexpiredRequestsArePendingOldestFirst()
    {
        var rig = new Rig();
        var joins = rig.Joins(new Probe(false, null));
        var body = (string session) => new JsonObject { ["team"] = "tm", ["harness"] = "my-harness", ["session_id"] = session, ["why"] = "because" };
        rig.Fx.Clock.UtcNow = T0;
        var a = await joins.OpenAsync(rig.Context, body("s1"), CancellationToken.None, T0.ToUnixTimeMilliseconds());
        rig.Fx.Clock.UtcNow = T0.AddSeconds(30);
        var b = await joins.OpenAsync(rig.Context, body("s2"), CancellationToken.None, T0.AddSeconds(30).ToUnixTimeMilliseconds());

        var both = joins.Pending(rig.Hierarchy, "tm", T0.AddSeconds(60).ToUnixTimeMilliseconds());
        var late = joins.Pending(rig.Hierarchy, "tm", T0.AddSeconds(630).ToUnixTimeMilliseconds());
        var otherTeam = joins.Pending(rig.Hierarchy, "other", T0.AddSeconds(60).ToUnixTimeMilliseconds());

        Assert.Equal([a.Value!["join_id"].AsString(), b.Value!["join_id"].AsString()], both.Select(p => p.Id).ToArray());
        Assert.Equal("because", both[0].Why);
        Assert.Equal([b.Value["join_id"].AsString()], late.Select(p => p.Id).ToArray());
        Assert.Empty(otherTeam);
    }
}
