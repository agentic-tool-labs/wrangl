using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah adr add</c> and <c>show</c> (adr-records A1), against a temp home's state database through the production composition.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class AdrVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly CliContext _context;
    private readonly string _cwd;

    public AdrVerbTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "my-cool-repo");
        var dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = dir, ["WRANGL_TEAM_FILE"] = Path.Combine(dir, "teams", "tm.json"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private (int Code, string Out, string Err) Run(Func<CliContext, string[], TextWriter, TextWriter, int> verb, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = verb(_context with { ReadStdin = () => "## Context\nwhy\n## Decision\nwhat\n## Consequences\nthen\n"u8.ToArray() }, [.. args, "--cwd", _cwd], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void AddThenShowRoundTripsEveryFieldAndFlagsAnEditedDocument()
    {
        var item = Run(WorkVerb.Run, "add", "Governed item", "--wave", "now");
        Assert.True(item.Code == 0, item.Err);
        var doc = Path.Combine(_cwd, "doc.md");
        File.WriteAllText(doc, "# the decision\n");

        var added = Run(AdrVerb.Run, "add", "Use one table", "--status", "accepted", "--body-file", "-", "--decision", "flag wins", "--governs", item.Out.Trim(), "--source", "prompt:p1", "--decided-by", "user", "--external", "doc.md", "--by", "tm-architect");
        Assert.True(added.Code == 0, added.Err);
        Assert.Equal("adr-1", added.Out.Trim());

        var show = Run(AdrVerb.Run, "show", "adr-1", "--json");
        Assert.True(show.Code == 0, show.Err);
        var shown = JsonText.Parse(show.Out);
        Assert.Equal("Use one table", shown.Member("title")!.Value.AsString());
        Assert.Equal("accepted", shown.Member("status")!.Value.AsString());
        Assert.Equal("user", shown.Member("decided_by")!.Value.AsString());
        Assert.Equal("prompt:p1", shown.Member("source")!.Value.AsString());
        Assert.Equal("tm-architect", shown.Member("by")!.Value.AsString());
        Assert.Equal(doc, shown.Member("external")!.Value.AsString());
        Assert.Equal("ok", shown.Member("document")!.Value.AsString());
        var summary = shown.Member("summary")!.Value;
        Assert.Equal(doc, summary.Member("authoritative")!.Value.AsString());
        Assert.Equal("why", summary.Member("context")!.Value.AsString());
        Assert.Equal("flag wins", summary.Member("decision")!.Value.AsString());
        Assert.Equal("then", summary.Member("consequences")!.Value.AsString());
        Assert.Equal("governs", shown.Member("links")!.Value.AsArray()[0].Member("kind")!.Value.AsString());
        Assert.Contains($"summary (authoritative: {doc})", Run(AdrVerb.Run, "show", "adr-1").Out, StringComparison.Ordinal);

        File.WriteAllText(doc, "# edited\n");
        Assert.Contains("document changed since ", Run(AdrVerb.Run, "show", "adr-1").Out, StringComparison.Ordinal);
        File.Delete(doc);
        Assert.Contains("document missing", Run(AdrVerb.Run, "show", "adr-1").Out, StringComparison.Ordinal);

        Assert.True(Run(AdrVerb.Run, "add", "A linked page", "--external", "https://example.invalid/adr").Code == 0);
        var url = Run(AdrVerb.Run, "show", "adr-2");
        Assert.Contains("summary (authoritative: https://example.invalid/adr)", url.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("document ", url.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void StateChangesLinksAndSyncKeepBothEndsAndTheItemHistory()
    {
        var item = Run(WorkVerb.Run, "add", "Governed item", "--wave", "now").Out.Trim();
        var doc = Path.Combine(_cwd, "doc.md");
        File.WriteAllText(doc, "v1\n");
        Assert.Equal("adr-1", Run(AdrVerb.Run, "add", "Waiting on the user", "--governs", item, "--decided-by", "user", "--external", "doc.md").Out.Trim());
        Assert.Equal("adr-2", Run(AdrVerb.Run, "add", "Unset decider").Out.Trim());
        Assert.Equal("adr-3", Run(AdrVerb.Run, "add", "Architect's own", "--decided-by", "architect").Out.Trim());
        Assert.Equal("adr-4", Run(AdrVerb.Run, "add", "Already ruled", "--status", "accepted", "--decided-by", "user").Out.Trim());

        Assert.Equal(["adr-2", "adr-1"], Run(AdrVerb.Run, "list", "--waiting").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Split(' ')[0]));
        Assert.Equal("adr-1", Run(AdrVerb.Run, "list", "--governs", item).Out.Split(' ')[0]);

        var settled = Run(AdrVerb.Run, "settle", "adr-4", "rejected");
        Assert.Equal(2, settled.Code);
        Assert.Contains("is accepted", settled.Err, StringComparison.Ordinal);

        File.WriteAllText(doc, "v2\n");
        var accepted = Run(AdrVerb.Run, "settle", "adr-1", "accepted", "--note", "ok by the user");
        Assert.Equal(0, accepted.Code);
        Assert.Contains("document changed since", accepted.Err, StringComparison.Ordinal);
        Assert.Equal(0, Run(AdrVerb.Run, "sync", "adr-1", "--decision", "now v2").Code);
        var synced = Run(AdrVerb.Run, "show", "adr-1");
        Assert.DoesNotContain("document changed", synced.Out, StringComparison.Ordinal);
        Assert.Contains("now v2", synced.Out, StringComparison.Ordinal);
        Assert.Contains("proposed → accepted: ok by the user", synced.Out, StringComparison.Ordinal);
        Assert.True(JsonText.Parse(Run(AdrVerb.Run, "show", "adr-1", "--json").Out).Member("summary")!.Value.Member("consequences")!.Value.IsNull);

        Assert.Equal(2, Run(AdrVerb.Run, "supersede", "adr-1", "--by", "adr-1").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "supersede", "adr-1", "--by", "adr-4").Code);
        Assert.Contains("(superseded)", Run(AdrVerb.Run, "show", "adr-1").Out, StringComparison.Ordinal);
        Assert.Contains("supersedes adr-1", Run(AdrVerb.Run, "show", "adr-4").Out, StringComparison.Ordinal);
        Assert.Contains("adr-4 supersedes adr-1", Run(AdrVerb.Run, "show", "adr-1").Out, StringComparison.Ordinal);

        Assert.Equal(0, Run(AdrVerb.Run, "link", "adr-2", "--relates", "adr-3").Code);
        Assert.Contains("relates adr-3", Run(AdrVerb.Run, "show", "adr-2").Out, StringComparison.Ordinal);
        Assert.Equal(0, Run(AdrVerb.Run, "deprecate", "adr-4").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "link", "adr-1", "--external", "doc.md", "--remove").Code);
        using (var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!)
        {
            Assert.Equal([null, null, null], reader.Query("SELECT external, external_sha256, external_read_ms FROM adrs WHERE n = 1")[0]);
        }

        var log = Run(WorkVerb.Run, "log", item).Out;
        Assert.Contains("adr-1 proposed: Waiting on the user", log, StringComparison.Ordinal);
        Assert.Contains("adr-1 accepted: Waiting on the user", log, StringComparison.Ordinal);
        Assert.Contains("adr-1 superseded: Waiting on the user", log, StringComparison.Ordinal);
        var history = Run(AdrVerb.Run, "show", "adr-1").Out;
        Assert.Contains("added proposed", history, StringComparison.Ordinal);
        Assert.Contains("accepted: sync", history, StringComparison.Ordinal);
        Assert.Contains("accepted → superseded: by adr-4", history, StringComparison.Ordinal);
        Assert.Contains("unlink external", history, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrchestratorWaitingListNamesTheAdrsWaitingOnTheUserAndNoOther()
    {
        var key = Ah.Hierarchy.HierarchyModule.WorkDirKey(Path.Combine(_cwd, ".claude", "ah2"));
        Assert.Null(AdrVerb.Waiting(_sandbox.AhHome, key));
        Assert.Equal(0, Run(AdrVerb.Run, "add", "Asked by the user", "--decided-by", "user").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "add", "Nobody named").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "add", "The architect decides", "--decided-by", "tm-architect").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "add", "Already ruled", "--status", "accepted", "--decided-by", "user").Code);
        Assert.Equal(0, Run(AdrVerb.Run, "add", "An older record", "--decided-by", "jim").Code);

        var waiting = AdrVerb.Waiting(_sandbox.AhHome, key)!;

        Assert.StartsWith("ADRs waiting on you (3), ", waiting, StringComparison.Ordinal);
        Assert.Contains("--decided-by user;", waiting, StringComparison.Ordinal);
        Assert.EndsWith("\n- adr-5 An older record\n- adr-2 Nobody named\n- adr-1 Asked by the user", waiting, StringComparison.Ordinal);
        Assert.Equal(["adr-5", "adr-2", "adr-1"], Run(AdrVerb.Run, "list", "--waiting").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Split(' ')[0]));
        Assert.Contains("adr-5 (proposed, waiting on you) An older record", Run(AdrVerb.Run, "list").Out, StringComparison.Ordinal);
        Assert.Contains("adr-3 (proposed) The architect decides", Run(AdrVerb.Run, "list").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreThatFailsTheWaitingQueryIsNoListAndAV36HomeGetsItsHistoryTableOnOpen()
    {
        var key = Ah.Hierarchy.HierarchyModule.WorkDirKey(Path.Combine(_cwd, ".claude", "ah2"));
        Assert.Equal(0, Run(AdrVerb.Run, "add", "Asked by the user").Code);
        var written = StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("ALTER TABLE adrs RENAME TO adrs_away");
            tx.Execute("DROP TABLE adr_history");
            return 0;
        });
        Assert.True(written.Ok, written.Message);

        Assert.Null(AdrVerb.Waiting(_sandbox.AhHome, key));

        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        using var reader = StateDbModule.OpenReader(_sandbox.AhHome).Reader!;
        Assert.Single(reader.Query("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'adr_history'"));
    }

    [Fact]
    public void ANearMissIsAUsageError()
    {
        Assert.Equal(2, Run(AdrVerb.Run, "show", "adr-999").Code);
        Assert.Equal(2, Run(AdrVerb.Run, "show", "adr-x").Code);
        Assert.Equal(2, Run(AdrVerb.Run, "add", "Too late", "--status", "rejected").Code);
        Assert.Equal(2, Run(AdrVerb.Run, "add", "Ungoverned", "--governs", "atp-999999").Code);
    }
}
