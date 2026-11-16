using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// Rule 9a (ah2-47): two members of a team who are assigned together (one work stream, or open requests on one item) message each
/// other with no link-request. The first send opens their link; it is extended at its caps, ends with the assignment, and stays closed
/// once the Orchestrator revokes it.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class AssignmentLinkTests
{
    private const string Team = "tm";

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    // a and b share stream s1; c is in s2 and d in none, and c and d hold open requests on one checklist item
    private const string TeamJson =
        """{"streams":{"s1":{"state":"open"},"s2":{"state":"open"}},"members":[{"name":"a","role":"implementor","kind":"claude","stream":"s1"},{"name":"b","role":"reviewer","kind":"claude","stream":"s1"},{"name":"c","role":"architect","kind":"claude","stream":"s2"},{"name":"d","role":"implementor","kind":"claude"}],"orchestrator":{"pid":1,"session_id":"o1"}}""";

    private static string Request(string id, string toName, string slug, string item, string from = "orchestrator", string team = Team, string? leads = null) =>
        $"---\nid: {id}\ntype: request\nto: implementor\nfrom: {from}\nslug: {slug}\nparent: null\nto_name: {toName}\nteam: {team}\nitem: {item}\n{(leads is null ? "" : $"from_leads: {leads}\n")}created: 2026-01-01T00:00:00+00:00\n---\n\nbody\n";

    private static (string File, string Text) Filed(string id, string toName, string slug, string item = "null", string from = "orchestrator", string team = Team, string? leads = null) =>
        ($"h/msgs/{id}--implementor--{slug}--request.md", Request(id, toName, slug, item, from, team, leads));

    private sealed class Mesh
    {
        private readonly MeshFixture _fx;
        private readonly string _dir;
        private int _n;

        internal Mesh(string? limits = null, params (string File, string Text)[] more)
        {
            var sandbox = UnitSandbox.Create();
            _dir = sandbox.Root + "/h";
            var env = new JsonObject { ["AH_HOME"] = "$ROOT/ah", ["WRANGL_HIERARCHY_DIR"] = "$ROOT/h" };
            if (limits is not null)
            {
                env["AH_LINK_LIMITS"] = limits;
            }

            var files = new JsonObject
            {
                ["h/teams/tm.json"] = TeamJson + "\n",
                ["h/teams/ot.json"] = """{"members":[{"name":"x","role":"implementor","kind":"claude"}],"orchestrator":{"pid":2147483000,"session_id":"o2"}}""" + "\n",
                ["h/msgs/20260101-000000-cc11--implementor--feat-c--request.md"] = Request("20260101-000000-cc11", "c", "feat-c", "it1"),
                ["h/msgs/20260101-000000-dd11--implementor--feat-d--request.md"] = Request("20260101-000000-dd11", "d", "feat-d", "it1"),
            };
            foreach (var (file, text) in more)
            {
                files[file] = text;
            }

            var c = new UnitCase("assignment", new JsonObject { ["env"] = env, ["files"] = files });
            sandbox.WriteFiles(c);
            _fx = MeshFixture.Create(sandbox.Environment(c), Now, sandbox.Root);
            foreach (var (name, team) in new[] { ("a", Team), ("b", Team), ("c", Team), ("d", Team), ("orchestrator", Team), ("x", "ot") })
            {
                var box = new BoxRef($"{name}@{team}", _fx.Mailbox.BoxDirFor($"{_dir}/teams/{team}.json", name));
                Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite($"s-{name}-{team}", [box], null, "claude", 1, PidStart: _fx.Table.StartTime(1))));
            }
        }

        internal MailboxOutcome<JsonObject> Send(string from, string to, string text = "hello", string kind = "note", string fromTeam = Team) =>
            _fx.Sender.Send(new JsonObject
            {
                ["cwd"] = _dir,
                ["hierarchy_dir"] = _dir,
                ["main_hierarchy_dir"] = JsonValue.Null,
                ["messaging_socket_dir"] = JsonValue.Null,
                ["to"] = to,
                ["from"] = $"{from}@{fromTeam}",
                ["session_id"] = $"s-{from}-{fromTeam}",
                ["id"] = $"20260101-1100{_n++:00}-ab12",
                ["kind"] = kind,
                ["text"] = text,
            });

        internal List<JsonObject> Links() => [.. _fx.Links.Judge(_dir, Team, Now.ToUnixTimeMilliseconds()).Select(l => l.AsObject())];

        internal void Revoke(string id)
        {
            var args = new JsonObject { ["dir"] = _dir, ["team"] = Team, ["id"] = id, ["reason"] = "revoked", ["now"] = Now.ToUnixTimeMilliseconds() };
            Assert.True(_fx.Writer.Invoke("links", "revoke", JsonText.ToUtf8(args, 0), new RequestContext(string.Empty, _dir, _dir, string.Empty), new StateCallOptions("rv")).Ok);
        }
    }

    private static string Text(JsonObject link, string key) => link.Get(key) is { IsText: true } t ? t.AsString() : string.Empty;

    [Fact]
    public void MembersOfOneStreamMessageEachOtherAndTwoSendsMakeOneLink()
    {
        var mesh = new Mesh();

        var first = mesh.Send("a", "b@tm");
        // a long message with no ref: a requested link would refuse it (link_needs_ref)
        var second = mesh.Send("b", "a@tm", new string('x', 400));

        Assert.True(first.Ok, first.Failure?.Message);
        Assert.True(second.Ok, second.Failure?.Message);
        var link = Assert.Single(mesh.Links());
        Assert.Equal("active", Text(link, "state"));
        Assert.Equal("stream:s1", Text(link, "assigned"));
        Assert.Equal("assignment", Text(link, "granted_by"));
        Assert.Equal(2, link.Get("used")!.Value.AsNumber());
    }

    [Fact]
    public void OpenRequestsOnOneItemLinkMembersOfDifferentStreamsOnTheSendersRequest()
    {
        var mesh = new Mesh();

        var sent = mesh.Send("c", "d@tm");

        Assert.True(sent.Ok, sent.Failure?.Message);
        var link = Assert.Single(mesh.Links());
        Assert.Equal("item:it1", Text(link, "assigned"));
        Assert.Equal("20260101-000000-cc11", Text(link, "task"));
    }

    [Fact]
    public void NegativeMembersWhoShareNothingStillNeedALinkAndNobodyLeavesTheTeam()
    {
        var mesh = new Mesh();

        Assert.Equal("not_linked", mesh.Send("a", "c@tm").Failure?.Code);
        Assert.Equal("outside_team", mesh.Send("a", "x@ot").Failure?.Code);
        Assert.Empty(mesh.Links());
    }

    [Fact]
    public void AnAssignmentLinkIsExtendedAtItsCapSoAssignedMembersAreNeverExhausted()
    {
        var mesh = new Mesh("""{"messages":2}""");

        for (var i = 0; i < 5; i++)
        {
            var sent = mesh.Send("a", "b@tm");
            Assert.True(sent.Ok, sent.Failure?.Message);
        }

        var link = Assert.Single(mesh.Links());
        Assert.Equal("active", Text(link, "state"));
        Assert.Equal(5, link.Get("used")!.Value.AsNumber());
        Assert.Equal(6, link.Get("limits")!.Value.Member("messages")!.Value.AsNumber());
        Assert.Equal(2, link.Get("extensions")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeALinkTheOrchestratorRevokedIsNotReopenedByTheSameAssignment()
    {
        var mesh = new Mesh();
        Assert.True(mesh.Send("a", "b@tm").Ok);

        mesh.Revoke(Text(Assert.Single(mesh.Links()), "id"));

        Assert.Equal("not_linked", mesh.Send("a", "b@tm").Failure?.Code);
        Assert.Equal("not_linked", mesh.Send("b", "a@tm").Failure?.Code);
        Assert.Equal("revoked", Text(Assert.Single(mesh.Links()), "state"));
    }

    [Fact]
    public void NegativeARequestAMemberFilesItselfAssignsNobody()
    {
        // c (dispatched on feat-c, item it1) files a request to a on its own slug and item: a was never dispatched on it
        var forged = new Mesh(null, Filed("20260101-000000-aa21", "a", "feat-c", "it1", from: "architect"));
        // and two requests a member made up, one to each, share a slug nobody dispatched
        var invented = new Mesh(null,
            Filed("20260101-000000-aa31", "a", "made-up", from: "implementor"),
            Filed("20260101-000000-cc31", "c", "made-up", from: "implementor"));

        Assert.Equal("not_linked", forged.Send("c", "a@tm").Failure?.Code);
        Assert.Equal("not_linked", forged.Send("a", "c@tm").Failure?.Code);
        Assert.Equal("not_linked", invented.Send("a", "c@tm").Failure?.Code);
        Assert.Empty(forged.Links());
        Assert.Empty(invented.Links());
    }

    [Fact]
    public void DispatchesOnOneSlugLinkTheirHoldersAlsoWhenTheyNameNoTeam()
    {
        // the Orchestrator's dispatches filed with no team (the default one) are still this team's: the names are its members
        var mesh = new Mesh(null,
            Filed("20260101-000000-aa41", "a", "shared", team: "null"),
            Filed("20260101-000000-cc41", "c", "shared", team: "null"));

        var sent = mesh.Send("a", "c@tm");

        Assert.True(sent.Ok, sent.Failure?.Message);
        var link = Assert.Single(mesh.Links());
        Assert.Equal("slug:shared", Text(link, "assigned"));
        Assert.Equal("20260101-000000-aa41", Text(link, "task"));
    }

    [Fact]
    public void NegativeALeadClassRequestAssignsOnlyWhenItsFilerLeadsTheTeam()
    {
        // ah2-49: a sub-orchestrator of child team ch files into its parent tm, and an off-duty deputy (leads none) files too
        var sub = new Mesh(null,
            Filed("20260101-000000-aa51", "a", "lane", from: "sub-orchestrator", leads: "ch"),
            Filed("20260101-000000-cc51", "c", "lane", from: "sub-orchestrator", leads: "ch"));
        var deputy = new Mesh(null,
            Filed("20260101-000000-aa61", "a", "duty", from: "deputy-chief", leads: "-"),
            Filed("20260101-000000-cc61", "c", "duty", from: "deputy-chief", leads: "-"));
        // tm's own lead (the orchestrator, or a deputy on duty holding orchestrator@tm) still assigns
        var lead = new Mesh(null,
            Filed("20260101-000000-aa71", "a", "led", from: "deputy-chief", leads: "ch,tm"),
            Filed("20260101-000000-cc71", "c", "led", leads: "tm"));

        Assert.Equal("not_linked", sub.Send("a", "c@tm").Failure?.Code);
        Assert.Equal("not_linked", deputy.Send("c", "a@tm").Failure?.Code);
        Assert.Empty(sub.Links());
        Assert.Empty(deputy.Links());
        var sent = lead.Send("a", "c@tm");
        Assert.True(sent.Ok, sent.Failure?.Message);
        Assert.Equal("slug:led", Text(Assert.Single(lead.Links()), "assigned"));
    }

    // ---------------------------------------------------------------- the rule alone, and the tick that ends a link

    private static Func<string, MeshTeam?> Lookup(string dir, string streamState) =>
        team => team == Team ? new MeshTeam(JsonText.Parse(TeamJson.Replace("\"s1\":{\"state\":\"open\"}", $"\"s1\":{{\"state\":\"{streamState}\"}}", StringComparison.Ordinal)), dir) : null;

    private sealed class TeamRecordOnly : IMeshFacts
    {
        public bool RelayLive(string entryId, string address) => false;

        public bool RepliesToBriefer(MeshParty sender, MeshParty recipient, string? msgKind) => false;

        public bool ChainReachesTask(string messagePath, string taskId, string dir, string team) => false;
    }

    [Theory]
    [InlineData("open", "note", true, "9a", null)]
    [InlineData("done", "note", false, "10", "not_linked")] // the stream is finished: the assignment is over
    [InlineData("open", "sendmessage", false, "9a", "link_needs_ref")] // a bare SendMessage never rides a link; the text names ah msg send
    [InlineData("open", "deliver", false, "9a", "link_needs_ref")] // nor does a paste into a pane (roster deliver): ahd does not carry it
    public void TheRuleAllowsAssignedMembersOnlyWhileTheStreamIsOpenAndOnlyThroughAhd(string stream, string kind, bool allowed, string rule, string? code)
    {
        var rig = new LinkCommandsTests.Rig();
        var a = new MeshParty("member", "a@tm", "a", Team, "implementor");
        var b = new MeshParty("member", "b@tm", "b", Team, "reviewer");

        var verdict = rig.Fixture.Rule.Decide(new MeshDecideRequest(Now.ToUnixTimeMilliseconds(), a, b, Lookup(rig.Dir, stream), "hello", kind, new TeamRecordOnly()));

        Assert.Equal(allowed, verdict.Allow);
        Assert.Equal(rule, verdict.Rule);
        Assert.Equal(code, verdict.Code);
        if (code == "link_needs_ref")
        {
            Assert.Contains("msg send b@tm", verdict.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeAnUltraAdvisorIsNeverLinkedByAssignment()
    {
        var rig = new LinkCommandsTests.Rig();
        var member = new MeshParty("member", "a@tm", "a", Team, "implementor");
        var advisor = new MeshParty("member", "b@tm", "b", Team, "ultra-advisor");
        MeshVerdict Decide(MeshParty from, MeshParty to) =>
            rig.Fixture.Rule.Decide(new MeshDecideRequest(Now.ToUnixTimeMilliseconds(), from, to, Lookup(rig.Dir, "open"), "hello", "note", new TeamRecordOnly()));

        // they share stream s1, and still its links go through the user's own answer
        Assert.Equal(("10", "not_linked"), (Decide(member, advisor).Rule, Decide(member, advisor).Code));
        Assert.Equal(("10", "not_linked"), (Decide(advisor, member).Rule, Decide(advisor, member).Code));
    }

    [Fact]
    public async Task ALinkRequestAfterAnAssignmentLinkStillOpensItsOwnLink()
    {
        var rig = new LinkCommandsTests.Rig();
        rig.WriteTask();
        var byAssignment = new JsonObject
        {
            ["task"] = "20260101-110000-t001", ["peers"] = new JsonArray { "rev@tm" }, ["requester"] = "imp@tm", ["direction"] = "both",
            ["granted_by"] = "assignment", ["assigned"] = "slug:tsk1",
        };
        rig.Effects.LinksSync("grant", new JsonObject { ["dir"] = rig.Dir, ["team"] = Team, ["link"] = byAssignment, ["now"] = Now.ToUnixTimeMilliseconds() }, "g1");

        // the same requester, peer, task and direction: without the exclusion this answers "existing" and no review-loop link is made
        var before = rig.Requests().Length;
        var asked = await rig.Run("imp@tm", "link-request", [], ("to", "rev@tm"), ("task", "20260101-110000-t001"), ("why", "need review"));

        Assert.True(asked.Ok, asked.Failure?.Message);
        Assert.False(asked.Value!.Result.AsObject().Get("existing") is { IsTruthy: true });
        Assert.Equal(before + 1, rig.Requests().Length); // its own link request was filed
    }

    [Fact]
    public void NegativeAGrantAfterTheOrchestratorsRevokeOpensNothing()
    {
        // the revoke landed between the rule's look and the grant: the grant itself respects it
        var rig = new LinkCommandsTests.Rig();
        JsonObject Grant(string opId) => rig.Effects.LinksSync(
            "grant",
            new JsonObject
            {
                ["dir"] = rig.Dir, ["team"] = Team, ["now"] = Now.ToUnixTimeMilliseconds(),
                ["link"] = new JsonObject { ["task"] = "stream-s1", ["peers"] = new JsonArray { "b@tm" }, ["requester"] = "a@tm", ["granted_by"] = "assignment", ["assigned"] = "stream:s1" },
            },
            opId);
        var id = Grant("g1").Get("id")!.Value.AsString();
        rig.Effects.LinksSync("revoke", new JsonObject { ["dir"] = rig.Dir, ["team"] = Team, ["id"] = id, ["reason"] = "revoked", ["now"] = Now.ToUnixTimeMilliseconds() }, "rv");

        var again = Grant("g2");

        Assert.True(again.Get("revoked") is { IsTruthy: true });
        Assert.Single(rig.Fixture.Links.ReadLinks(rig.Dir, Team).Get("links")!.Value.AsArray());
    }

    [Fact]
    public void ARequestedLinkKeepsItsShortNotesAndTheAssignmentTakesWhatItWouldRefuse()
    {
        var rig = new LinkCommandsTests.Rig();
        var requested = new JsonObject { ["task"] = "20260101-110000-t001", ["peers"] = new JsonArray { "b@tm" }, ["requester"] = "a@tm", ["granted_by"] = "orchestrator" };
        var id = rig.Effects.LinksSync("grant", new JsonObject { ["dir"] = rig.Dir, ["team"] = Team, ["link"] = requested, ["now"] = Now.ToUnixTimeMilliseconds() }, "g1").Get("id")!.Value.AsString();
        var a = new MeshParty("member", "a@tm", "a", Team, "implementor");
        var b = new MeshParty("member", "b@tm", "b", Team, "reviewer");
        MeshVerdict Decide(string text) =>
            rig.Fixture.Rule.Decide(new MeshDecideRequest(Now.ToUnixTimeMilliseconds(), a, b, Lookup(rig.Dir, "open"), text, "note", new TeamRecordOnly()));

        var shortNote = Decide("hello");
        var longNote = Decide(new string('x', 400));

        // the requested link (a review loop's, say) still counts its own traffic
        Assert.Equal(("9", id), (shortNote.Rule, shortNote.Link?.AsString()));
        // what it would refuse with link_needs_ref goes by the assignment, on a link of its own
        Assert.Equal(("9a", true, "stream:s1"), (longNote.Rule, longNote.Allow, longNote.Assigned));
        Assert.Null(longNote.Link);
    }

    [Fact]
    public async Task TheTickEndsAnAssignmentLinkWhenTheStreamIsDoneAndLeavesItWhileItIsOpen()
    {
        var rig = new LinkCommandsTests.Rig();
        var link = new JsonObject { ["task"] = "stream-s1", ["peers"] = new JsonArray { "b@tm" }, ["requester"] = "a@tm", ["granted_by"] = "assignment", ["assigned"] = "stream:s1" };
        var id = rig.Effects.LinksSync("grant", new JsonObject { ["dir"] = rig.Dir, ["team"] = Team, ["link"] = link, ["now"] = Now.ToUnixTimeMilliseconds() }, "g1").Get("id")!.Value.AsString();

        var whileOpen = await rig.Commands.RevokeDeadRunLinksAsync(rig.Context, Lookup(rig.Dir, "open"), Team, CancellationToken.None, Now.ToUnixTimeMilliseconds());
        var whenDone = await rig.Commands.RevokeDeadRunLinksAsync(rig.Context, Lookup(rig.Dir, "done"), Team, CancellationToken.None, Now.ToUnixTimeMilliseconds());

        Assert.Equal(0, whileOpen);
        Assert.Equal(1, whenDone);
        var ended = rig.Fixture.Links.ReadLinks(rig.Dir, Team).Get("links")!.Value.AsArray().Single(l => l.Member("id")!.Value.AsString() == id);
        Assert.Equal("revoked", ended.Member("state")!.Value.AsString());
        Assert.Equal("assignment_ended", ended.Member("end_reason")!.Value.AsString());
    }
}
