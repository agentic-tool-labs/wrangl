using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A request made with <c>msg new</c> and sent as a brief is an open dispatch for the overdue watcher until its response lands; a note is never one.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class OverdueBriefDispatchTests : IDisposable
{
    private const string Team = "tm";
    private const string RequestId = "20261016-140001-abcd";
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;

    public OverdueBriefDispatchTests()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
            ["AH_SLOTS_DIR"] = Path.Combine(_sandbox.Root, "slots"),
        };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var teams = Path.Combine(Hierarchy, "teams");
        Directory.CreateDirectory(teams);
        var manager = Path.Combine(teams, Team + ".json");
        File.WriteAllText(
            manager,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\"},"
            + "\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}],\"streams\":{}}\n");
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", manager, "orchestrator@" + Team);
        FakeRunner.RegisterSelf(_graph.Mailbox, "m", manager, "impl-1@" + Team);
    }

    public void Dispose() => _sandbox.Dispose();

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    private string Run(params string[] args)
    {
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.True(_sandbox.Mirrored(() => MsgVerb.Run(context, [.. args, "--session", "o", "--plain", "--cwd", _sandbox.Root], stdout, stderr)) == 0, stderr.ToString());
        return stdout.ToString();
    }

    private string NewRequest(string from, string id = RequestId)
    {
        var made = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--slug", "t1", "--title", "A task", "--id", id, "--team", Team);
        var path = _sandbox.MsgFile(made);
        var edited = File.ReadAllText(path).Replace("from: orchestrator", "from: " + from, StringComparison.Ordinal);

        // the store holds the sender in its row: the hand edit is stored again as the message it now is
        Ah.TestKit.TestMsgStore.Delete(_sandbox.AhHome, path);
        return Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, path, edited);
    }

    private IReadOnlyList<Ah.Watch.OverdueDispatch> Open() =>
        _sandbox.Mirrored(() => new WakeComposition(_graph, _env).OpenDispatches(Hierarchy, Team, "o"));

    private void NameAddressee(string request, string name)
    {
        var edited = File.ReadAllText(request).Replace("to_name: null", "to_name: " + name, StringComparison.Ordinal);
        Ah.TestKit.TestMsgStore.Delete(_sandbox.AhHome, request);
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, request, edited);
    }

    [Theory]
    [InlineData("not-spawned", false)]
    [InlineData("impl-1", true)]
    public void AnUnsentRequestIsWatchedOnlyWhenItsNamedMemberHasASession(string name, bool watched)
    {
        NameAddressee(NewRequest("orchestrator"), name);

        Assert.Equal(watched, Open().Any(d => d.RequestId == RequestId));
    }

    [Fact]
    public void APausedMembersBriefIsNotWatchedAndAResumedOneIsWatchedFromTheResume()
    {
        NameAddressee(NewRequest("orchestrator"), "impl-1");
        using (Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
        }

        _graph.Roster.SetPause(Hierarchy, Team, "impl-1", true, 1000);
        Assert.DoesNotContain(Open(), static d => d.RequestId == RequestId);

        var resumedAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        _graph.Roster.SetPause(Hierarchy, Team, "impl-1", false, resumedAt);
        Assert.Equal(resumedAt, Assert.Single(Open(), static d => d.RequestId == RequestId).FloorMs);
    }

    [Theory]
    [InlineData("orchestrator")]
    [InlineData("sub-orchestrator")]
    public void ABriefOfARequestIsListedWithItsAddresseeAndClearsWhenTheResponseLands(string from)
    {
        var request = NewRequest(from);
        Assert.Empty(Open());

        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");

        var dispatch = Assert.Single(Open());
        Assert.Equal("impl-1", dispatch.ToName);
        Assert.Equal(RequestId, dispatch.RequestId);

        File.WriteAllText(request.Replace("--request.md", "--response.md", StringComparison.Ordinal), $"---\nid: {RequestId}\ntype: response\n---\n\n## [0] tldr\n- [1] status: DONE\n");

        Assert.Empty(Open());
    }

    [Fact]
    public void NegativeANoteNeverCountsAndARequestFromAnotherRoleIsNotCountedEvenWhenBriefed()
    {
        var request = NewRequest("implementor");
        _ = Run("send", "impl-1@" + Team, "--ref", request, "--kind", "brief");
        _ = Run("send", "impl-1@" + Team, "--text", "just a note", "--kind", "note");

        Assert.Empty(Open());
    }

    [Fact]
    public void NegativeABriefOfAnotherRequestGivesThisOneNoAddressee()
    {
        _ = NewRequest("orchestrator");
        const string OtherId = "20261016-140002-abce";
        var made = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--slug", "t2", "--title", "Other task", "--id", OtherId, "--team", Team);
        var other = _sandbox.MsgFile(made);

        _ = Run("send", "impl-1@" + Team, "--ref", other, "--kind", "brief");

        Assert.Equal(OtherId, Assert.Single(Open()).RequestId);
    }

    /// <summary>b271: the watch keeps the brief listing across ticks while no mail is written; an ack is a write, so the next tick reads the brief as read.</summary>
    [Fact]
    public void ABriefKeptAcrossTicksIsReadOnceItsEntryIsAcked()
    {
        // the derived rows skip an id stamped past the horizon: this request is made now
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcd";
        _ = Run("send", "impl-1@" + Team, "--ref", NewRequest("orchestrator", fresh), "--kind", "brief");

        // both sessions are this process; the watch reads the orchestrator's teams only while its registration is the live one
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", Path.Combine(Hierarchy, "teams", Team + ".json"), "orchestrator@" + Team);
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool Unread(double at) => Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", at))!, c => c.RequestId == fresh).BriefUnread;
        Assert.NotNull(_graph.Mailbox.MailChangeToken());
        Assert.True(Unread(now));
        Assert.True(Unread(now + 1));

        var box = Assert.Single(_graph.Mailbox.ListBoxes(), static b => b.Address == "impl-1@" + Team);
        var id = Assert.Single(_graph.Mailbox.ReadEntries(box.Dir).Entries).Member("id")!.Value.AsString();
        Assert.Single(_graph.Mailbox.AckIds(box.Dir, [id], "impl-1").Acked);

        Assert.False(Unread(now + 2));
    }

    /// <summary>
    /// b283: the watch skips the box of a member with no roster row, no live exchange and no entry inside the horizon. Still read: the box of a dismissed
    /// member a live request names, and one whose brief is of a request the read set does not attribute to it (the foreign-pool path).
    /// </summary>
    [Theory]
    [InlineData("gone-2", false, true)]
    [InlineData("gone-1", false, true)]
    [InlineData("gone-1", true, false)]
    public void AClosedMembersBoxIsSkippedButNotOneALiveRequestNames(string box, bool oldEntry, bool read)
    {
        // gone-1 and gone-2 have no roster row; the open request is to gone-2, its brief sits in the box under test
        var fresh = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-abcd";
        var request = NewRequest("orchestrator", fresh);
        NameAddressee(request, "gone-2");
        var teamFile = Path.Combine(Hierarchy, "teams", Team + ".json");
        if (oldEntry)
        {
            // the box's last mail is a week old: planted, since a send stamps its entry now
            var entry = new Ah.Core.Json.JsonObject { ["id"] = "20261001-000000-0old", ["kind"] = "brief", ["ref"] = request, ["from"] = "orchestrator@" + Team, ["to"] = box + "@" + Team };
            Ah.TestKit.TestPlant.Mail(_graph.Writer, _graph.Mailbox.BoxDirFor(teamFile, box), entry, _sandbox.Root);
        }
        else
        {
            var roster = File.ReadAllText(teamFile);
            const string Row = "{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"claude\"}";
            _sandbox.PlantTeam(teamFile, roster.Replace(Row, Row + "," + Row.Replace("impl-1", box, StringComparison.Ordinal), StringComparison.Ordinal));
            _ = Run("send", box + "@" + Team, "--ref", request, "--kind", "brief");
            _sandbox.PlantTeam(teamFile, roster); // dismissed: the row is gone, the box stays
        }

        FakeRunner.RegisterSelf(_graph.Mailbox, "o", teamFile, "orchestrator@" + Team);
        var host = new WatchHostAdapter(_graph, Ah.Roster.RosterModule.CreateScreenRules(), _graph.Io.Clock, WatchHostSeams.Unavailable);
        var candidate = Assert.Single(_sandbox.Mirrored(() => host.Dispatches("o", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))!, c => c.RequestId == fresh);

        Assert.Equal(read ? box + "@" + Team : null, candidate.ToAddr);
    }
}
