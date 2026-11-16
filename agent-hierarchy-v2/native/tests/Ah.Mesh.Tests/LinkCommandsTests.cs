using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// The link commands over the real file system of a sandbox and the real <c>links</c> domain: a request is a file, a decision is the
/// exclusive creation of its response, and the effect follows the claim.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class LinkCommandsTests
{
    private const string Team = "tm";
    private const string TaskId = "20260101-110000-t001";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    internal sealed class Rig
    {
        private readonly MeshFixture _fx;
        private readonly string _root;

        /// <summary>A rig in files mode, or with <paramref name="rows"/> in db mode, where each message file the rig writes is also a row.</summary>
        internal Rig(IReadOnlyList<(string Name, string Role)>? members = null, IRunFacts? runFacts = null, RowMsgs? rows = null)
        {
            var sandbox = UnitSandbox.Create();
            _root = sandbox.Root;
            Dir = _root + "/h";
            Directory.CreateDirectory(Dir + "/msgs");
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = _root + "/ah" };
            Rows = rows;
            _fx = MeshFixture.Create(vars, Start, _root, msgsRoute: rows?.Route());
            var list = new JsonArray();
            foreach (var (name, role) in members ?? [("imp", "implementor"), ("rev", "reviewer"), ("arc", "architect"), ("adv", "ultra-advisor")])
            {
                list.Add(new JsonObject { ["name"] = name, ["role"] = role });
            }

            var data = new JsonObject { ["members"] = list };
            Lookup = team => team == Team ? new MeshTeam(data, Dir) : null;
            Effects = new Effects(this);
            Commands = MeshModule.CreateLinkCommands(
                _fx.Rule, _fx.Links, _fx.Hierarchy.Core, _fx.Hierarchy.Messages, _fx.Files, _fx.Clock, Effects, Effects, runFacts);
        }

        internal string Dir { get; }

        internal RowMsgs? Rows { get; }

        /// <summary>Writes a message file and, in db mode, its row.</summary>
        internal void WriteMessage(string path, string text)
        {
            File.WriteAllText(path, text);
            Rows?.Add(Path.GetDirectoryName(Path.GetDirectoryName(path))!, Path.GetFileName(path), text);
        }

        internal Func<string, MeshTeam?> Lookup { get; }

        internal Effects Effects { get; }

        internal ILinkCommands Commands { get; }

        internal MeshFixture Fixture => _fx;

        internal LinkContext Context => new(Dir, Dir);

        internal void WriteTask(string id = TaskId, string from = "orchestrator", string to = "implementor", string? toName = "imp", bool answered = false, string marker = "")
        {
            WriteMessage(
                $"{Dir}/msgs/{id}--{to}--tsk1--request.md",
                $"---\nid: {id}\ntype: request\nto: {to}\nfrom: {from}\nslug: tsk1\nto_name: {toName ?? "null"}\nteam: {Team}\n---\n\n## [0] tldr\n- do it\n");
            if (answered)
            {
                WriteMessage($"{Dir}/msgs/{id}--orchestrator--tsk1--response.md", $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: {to}\nslug: tsk1\n{(marker.Length > 0 ? marker + "\n" : string.Empty)}---\n\n## [0] tldr\n- done\n");
            }
        }

        internal Task<MailboxOutcome<LinkCommandResult>> Run(string address, string command, string[] positional, params (string Key, string Value)[] opts) =>
            Commands.RunAsync(
                new LinkCommandRequest(command, positional, opts.ToDictionary(o => o.Key, o => o.Value), Context, Lookup, [address]),
                CancellationToken.None);

        internal async Task<string> OpenLinkRequest(string peers = "rev@tm")
        {
            var r = await Run("imp@tm", "link-request", [], ("to", peers), ("task", TaskId), ("why", "need review"));
            Assert.True(r.Ok, r.Failure?.Message);
            return r.Value!.Text;
        }

        internal string[] Responses() => Directory.GetFiles(Dir + "/msgs", "*--response.md").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

        internal string[] Requests() => Directory.GetFiles(Dir + "/msgs", "*--request.md").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    internal sealed class Effects(Rig rig) : ILinkEffects, ILinkMirror
    {
        internal List<LinkNotice> Notices { get; } = [];

        internal List<LinkMirror> Mirrors { get; } = [];

        internal List<string> Ops { get; } = [];

        internal List<bool> TargetContexts { get; } = [];

        internal JsonObject LinksSync(string op, JsonObject args, string? opId)
        {
            Ops.Add(op);
            var ctx = new RequestContext(string.Empty, rig.Dir, rig.Dir, string.Empty);
            var result = rig.Fixture.Writer.Invoke("links", op, JsonText.ToUtf8(args, 0), ctx, opId is null ? null : new StateCallOptions(opId));
            Assert.True(result.Ok, result.Error);
            return JsonText.Parse(result.ResultJson.ToArray()).AsObject();
        }

        public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation) =>
            Task.FromResult(LinksSync(op, args, opId));

        public bool RefuseAsOutsideTheCwd { get; set; }

        public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false)
        {
            if (RefuseAsOutsideTheCwd)
            {
                throw new InvalidOperationException($"path {path} is outside the request's hierarchy directories");
            }

            TargetContexts.Add(ownPoolContext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(content);
                }

                rig.Rows?.Add(Path.GetDirectoryName(Path.GetDirectoryName(path))!, Path.GetFileName(path), content);
                return Task.FromResult(true);
            }
            catch (IOException) when (File.Exists(path))
            {
                return Task.FromResult(false);
            }
        }

        public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }

        public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation)
        {
            Mirrors.Add(mirror);
            if (mirror.N != 0)
            {
                var name = $"{mirror.EntryId}--member--ln-{mirror.Task}--link-note.md";
                var n = mirror.N is { } k ? k.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
                rig.WriteMessage(
                    Path.Combine(mirror.TaskHome, "msgs", name),
                    $"---\nid: {mirror.EntryId}\ntype: link-note\nto: member\nfrom: {mirror.FromAddress}\nslug: ln\nparent: {mirror.Task}\nlink: {mirror.Link}\nn: {n}\nto_address: {mirror.ToAddress}\ncreated: 2026-01-01T12:00:0{Mirrors.Count}\n---\n\n{mirror.Text}\n");
            }

            return Task.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- link-request

    [Fact]
    public async Task AMembersLinkRequestWritesTheRequestFileAndTellsTheOrchestrator()
    {
        var rig = new Rig();
        rig.WriteTask();

        var id = await rig.OpenLinkRequest("rev@tm");

        var files = rig.Requests();
        Assert.Contains(files, f => f.StartsWith(id + "--orchestrator--lreq-", StringComparison.Ordinal));
        var text = File.ReadAllText($"{rig.Dir}/msgs/{files.Single(f => f.StartsWith(id, StringComparison.Ordinal))}");
        Assert.Contains("subject: link\n", text, StringComparison.Ordinal);
        Assert.Contains("requester: imp@tm\n", text, StringComparison.Ordinal);
        Assert.Contains($"parent: {TaskId}\n", text, StringComparison.Ordinal);
        Assert.Contains("peers: rev@tm\n", text, StringComparison.Ordinal);
        var notice = Assert.Single(rig.Effects.Notices, n => n.Event == "link-request");
        Assert.Equal(("orchestrator@tm", "imp@tm", "event", "link-request"), (notice.To, notice.From, notice.Kind, notice.Event));
        var body = JsonText.Parse(notice.Text).AsObject();
        Assert.Equal(id, body["request"].AsString());
        Assert.False(body.Get("hint") is not null);
    }

    [Fact]
    public async Task ALinkRequestForAClosedTaskCarriesTheHint()
    {
        var rig = new Rig();
        rig.WriteTask(answered: true, marker: "closed: true");

        await rig.OpenLinkRequest();

        Assert.Equal("task_closed", JsonText.Parse(rig.Effects.Notices.First().Text).AsObject()["hint"].AsString());
    }

    [Fact]
    public async Task ATaskWithAMembersReportStaysLinkableAndALinkRequestedThenIsGranted()
    {
        var rig = new Rig();
        rig.WriteTask(answered: true);

        await rig.OpenLinkRequest();

        Assert.Null(JsonText.Parse(rig.Effects.Notices.First().Text).AsObject().Get("hint"));
        Assert.Contains("decision: granted\n", File.ReadAllText($"{rig.Dir}/msgs/{rig.Responses().Single(f => f.Contains("--lreq-", StringComparison.Ordinal))}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedLinkRequestWhileTheLinkIsLiveReturnsThatLinkAndOpensNoSecondOne()
    {
        var rig = new Rig();
        rig.WriteTask();

        var first = await rig.OpenLinkRequest();
        var second = await rig.OpenLinkRequest();

        Assert.Equal(first, second);
        Assert.Single(rig.Effects.Ops, op => op == "grant");
        Assert.Single(rig.Effects.Notices, n => n.Event == "link-request");
    }

    [Theory]
    [InlineData("closed: true")]
    [InlineData("withdrawn: true")]
    public async Task NegativeATaskClosedOrWithdrawnByItsLeadCarriesTheHintAndIsNotGranted(string marker)
    {
        var rig = new Rig();
        rig.WriteTask(answered: true, marker: marker);

        await rig.OpenLinkRequest();

        Assert.Equal("task_closed", JsonText.Parse(rig.Effects.Notices.First().Text).AsObject()["hint"].AsString());
        Assert.Contains("decision: denied\n", File.ReadAllText($"{rig.Dir}/msgs/{rig.Responses().Single(f => f.Contains("--lreq-", StringComparison.Ordinal))}"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orchestrator@tm", "rev@tm", TaskId, "why", "is a member's")]
    [InlineData("imp@tm", "", TaskId, "why", "needs --to")]
    [InlineData("imp@tm", "rev@tm", "", "why", "needs --task")]
    [InlineData("imp@tm", "rev@tm", TaskId, "   ", "needs --why")]
    [InlineData("imp@tm", "stranger@tm", TaskId, "why", "is not a member of tm")]
    [InlineData("imp@tm", "rev@other", TaskId, "why", "is not a member of tm")]
    [InlineData("imp@tm", "imp@tm", TaskId, "why", "other than yourself")]
    public async Task ALinkRequestThatBreaksARuleIsBadRequestAndWritesNothing(string caller, string to, string task, string why, string fragment)
    {
        var rig = new Rig();
        rig.WriteTask();

        var r = await rig.Run(caller, "link-request", [], ("to", to), ("task", task), ("why", why));

        Assert.False(r.Ok);
        Assert.Equal("bad_request", r.Failure!.Code);
        Assert.Contains(fragment, r.Failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Requests(), f => f.Contains("--lreq-", StringComparison.Ordinal));
        Assert.Empty(rig.Effects.Notices);
    }

    // ---------------------------------------------------------------- grant, deny

    [Fact]
    public async Task GrantingAnUnknownRequestIsUnknownRequest()
    {
        var rig = new Rig();

        var r = await rig.Run("orchestrator@tm", "link", ["grant", "20260101-000000-zzzz"]);

        Assert.Equal("unknown_request", r.Failure!.Code);
    }

    [Fact]
    public async Task AnUnknownSubcommandIsAUsageError()
    {
        var rig = new Rig();

        var r = await rig.Run("orchestrator@tm", "link", ["frobnicate", "x"]);

        Assert.Equal("bad_request", r.Failure!.Code);
        Assert.StartsWith("usage: ah msg link", r.Failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinkSubcommandWithoutAnIdNamesTheMissingId()
    {
        var rig = new Rig();

        var r = await rig.Run("orchestrator@tm", "link", ["grant"]);

        Assert.Equal("link grant needs an id", r.Failure!.Message);
    }

    // ---------------------------------------------------------------- auto-grant

    [Fact]
    public async Task AutoGrantOfAnUnknownOrDecidedRequestIsRefused()
    {
        var rig = new Rig();
        rig.WriteTask();
        var id = await rig.OpenLinkRequest();
        await rig.Run("orchestrator@tm", "link", ["grant", id]);

        var decided = await rig.Commands.AutoGrantAsync(rig.Context, rig.Lookup, Team, id, CancellationToken.None);
        var unknown = await rig.Commands.AutoGrantAsync(rig.Context, rig.Lookup, Team, "20260101-000000-zzzz", CancellationToken.None);

        Assert.Equal(new AutoGrantResult(false, "already decided"), decided);
        Assert.Equal(new AutoGrantResult(false, "unknown request"), unknown);
    }

    // ---------------------------------------------------------------- the lifecycle of a granted link

    private static async Task<(Rig Rig, string Link)> GrantedAsync(RowMsgs? rows = null)
    {
        var rig = new Rig(rows: rows);
        rig.WriteTask();
        var id = await rig.OpenLinkRequest();
        rig.Effects.Notices.Clear();
        rig.Effects.Mirrors.Clear();
        return (rig, $"lk-{id}");
    }

    [Fact]
    public async Task APostIsMirroredUnnumberedAndDeliveredToEveryEnd()
    {
        var (rig, link) = await GrantedAsync();

        var posted = await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "keep going"));

        Assert.True(posted.Ok, posted.Failure?.Message);
        Assert.Equal("posted to imp@tm, rev@tm", posted.Value!.Text);
        var mirror = Assert.Single(rig.Effects.Mirrors);
        Assert.Null(mirror.N);
        Assert.Equal("orchestrator@tm", mirror.FromAddress);
        Assert.Equal("imp@tm,rev@tm", mirror.ToAddress);
        Assert.Equal(["imp@tm", "rev@tm"], rig.Effects.Notices.Select(n => n.To).ToArray());
        Assert.All(rig.Effects.Notices, n => Assert.Equal(link, n.PostLink));
    }

    [Fact]
    public async Task APostNeedsTheOrchestratorAndATextAndAKnownLink()
    {
        var (rig, link) = await GrantedAsync();

        var member = await rig.Run("imp@tm", "link", ["post", link], ("text", "x"));
        var noText = await rig.Run("orchestrator@tm", "link", ["post", link]);
        var unknown = await rig.Run("orchestrator@tm", "link", ["post", "lk-nope"], ("text", "x"));

        Assert.Equal("orchestrator_only", member.Failure!.Code);
        Assert.Equal("bad_request", noText.Failure!.Code);
        Assert.Equal("unknown_link", unknown.Failure!.Code);
        Assert.Empty(rig.Effects.Mirrors);
        Assert.Empty(rig.Effects.Notices);
    }

    [Fact]
    public async Task InDbModeShowListsTheMirroredMessagesFromTheRowsWhenTheirFilesAreGone()
    {
        var rows = new RowMsgs();
        var (rig, link) = await GrantedAsync(rows);
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "one"));
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "two"));
        foreach (var note in Directory.GetFiles(rig.Dir + "/msgs", "*--link-note.md"))
        {
            File.Delete(note);
        }

        var all = await rig.Run("imp@tm", "link", ["show", link]);

        Assert.True(all.Ok, all.Failure?.Message);
        Assert.Equal(["one", "two"], all.Value!.Result.AsObject()["messages"].AsArray().Select(r => r.AsObject()["text"].AsString()).ToArray());
    }

    [Fact]
    public async Task NegativeInDbModeAnArchivedLinkNoteIsNotShown()
    {
        var rows = new RowMsgs();
        var (rig, link) = await GrantedAsync(rows);
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "one"));
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "two"));
        var first = rows.Rows.FindIndex(r => r.Type == "link-note");
        rows.Rows[first] = rows.Rows[first] with { Archived = true };

        var all = await rig.Run("imp@tm", "link", ["show", link]);

        Assert.Equal(["two"], all.Value!.Result.AsObject()["messages"].AsArray().Select(r => r.AsObject()["text"].AsString()).ToArray());
    }

    [Fact]
    public async Task ShowListsTheMirroredMessagesNumberedFromOneAndHonoursSinceAndTail()
    {
        var (rig, link) = await GrantedAsync();
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "one"));
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "two"));
        await rig.Run("orchestrator@tm", "link", ["post", link], ("text", "three"));

        var all = await rig.Run("imp@tm", "link", ["show", link]);
        var since = await rig.Run("imp@tm", "link", ["show", link], ("since", "2"));
        var tail = await rig.Run("imp@tm", "link", ["show", link], ("tail", "1"));

        Assert.True(all.Ok, all.Failure?.Message);
        var rows = all.Value!.Result.AsObject()["messages"].AsArray();
        Assert.Equal(["one", "two", "three"], rows.Select(r => r.AsObject()["text"].AsString()).ToArray());
        Assert.Equal([1, 2, 3], rows.Select(r => (int)r.AsObject()["n"].AsNumber()).ToArray());
        Assert.Equal(["three"], since.Value!.Result.AsObject()["messages"].AsArray().Select(r => r.AsObject()["text"].AsString()).ToArray());
        Assert.Equal(["three"], tail.Value!.Result.AsObject()["messages"].AsArray().Select(r => r.AsObject()["text"].AsString()).ToArray());
        Assert.Contains("#1  orchestrator@tm -> imp@tm,rev@tm", all.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowIsForTheOrchestratorAndTheLinksPeersOnly()
    {
        var (rig, link) = await GrantedAsync();

        var stranger = await rig.Run("arc@tm", "link", ["show", link]);
        var peer = await rig.Run("rev@tm", "link", ["show", link]);

        Assert.Equal("orchestrator_only", stranger.Failure!.Code);
        Assert.True(peer.Ok, peer.Failure?.Message);
    }

    private static (Func<string, MeshTeam?> Lookup, string OtherDir) WithAnotherTeamOwnedByTheCaller(Rig rig)
    {
        var otherDir = Path.GetDirectoryName(rig.Dir)! + "/o";
        Directory.CreateDirectory(otherDir + "/msgs");
        var other = new MeshTeam(new JsonObject { ["members"] = new JsonArray() }, otherDir);
        return (team => team == "zz" ? other : rig.Lookup(team), otherDir);
    }

    private static void DropTeamOf(Rig rig, string idPrefix)
    {
        var path = rig.Dir + "/msgs/" + rig.Requests().Single(n => n.StartsWith(idPrefix, StringComparison.Ordinal));
        var text = File.ReadAllText(path);
        Assert.Contains("\nteam: tm\n", text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace("\nteam: tm\n", "\nteam: null\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequestThatNamesNoTeamIsRefusedWithAClearMessageUnderEitherOfTwoNamedTeams()
    {
        var rig = new Rig();
        rig.WriteTask();
        var id = await rig.OpenLinkRequest();
        DropTeamOf(rig, id);
        var before = rig.Responses();
        var (lookup, _) = WithAnotherTeamOwnedByTheCaller(rig);

        foreach (var team in new[] { "tm", "zz" })
        {
            var granted = await rig.Commands.RunAsync(
                new LinkCommandRequest("link", ["grant", id], new Dictionary<string, string> { ["team"] = team }, rig.Context, lookup, ["orchestrator@zz", "orchestrator@tm"]),
                CancellationToken.None);
            Assert.False(granted.Ok, $"a teamless request must not be granted under {team}");
            Assert.Equal("teamless_request", granted.Failure?.Code);
            Assert.Contains("names no team; re-send it with --team <team>", granted.Failure?.Message, StringComparison.Ordinal);

            var denied = await rig.Commands.RunAsync(
                new LinkCommandRequest("link", ["deny", id], new Dictionary<string, string> { ["team"] = team, ["why"] = "no" }, rig.Context, lookup, ["orchestrator@zz", "orchestrator@tm"]),
                CancellationToken.None);
            Assert.False(denied.Ok);
            Assert.Equal("teamless_request", denied.Failure?.Code);
        }

        Assert.Equal(before, rig.Responses());
    }

    [Fact]
    public async Task ALinkRequestNamingATaskOfAnotherTeamCarriesTheUnknownTaskHint()
    {
        var rig = new Rig();
        rig.WriteTask();
        var taskPath = rig.Dir + "/msgs/" + rig.Requests().Single();
        File.WriteAllText(taskPath, File.ReadAllText(taskPath).Replace("\nteam: tm\n", "\nteam: zz\n", StringComparison.Ordinal));

        var r = await rig.Run("imp@tm", "link-request", [], ("to", "rev@tm"), ("task", TaskId), ("why", "need review"));

        Assert.True(r.Ok);
        Assert.Equal("task_unknown", JsonText.Parse(rig.Effects.Notices.First().Text).AsObject()["hint"].AsString());
    }

    [Fact]
    public async Task ListWithNoTeamMatchesTheContextDirectoryThroughASymlinkAndATrailingSlash()
    {
        var (rig, link) = await GrantedAsync();
        var linked = rig.Dir + "-linked";
        Directory.CreateSymbolicLink(linked, rig.Dir);
        var context = new LinkContext(linked + "/", linked + "/");

        var listed = await rig.Commands.RunAsync(
            new LinkCommandRequest("link", ["list"], new Dictionary<string, string>(), context, rig.Lookup, ["orchestrator@tm"]),
            CancellationToken.None);

        Assert.True(listed.Ok, listed.Failure?.Message);
        Assert.Equal([link], listed.Value!.Result.AsObject()["links"].AsArray().Select(l => l.AsObject()["id"].AsString()));
    }

    [Fact]
    public async Task ListWithNoTeamStillSkipsATeamWhoseDirectoryIsAnotherDirectory()
    {
        var (rig, _) = await GrantedAsync();
        var elsewhere = Path.GetDirectoryName(rig.Dir)! + "/elsewhere";
        Directory.CreateDirectory(elsewhere + "/msgs");
        var context = new LinkContext(elsewhere, elsewhere);

        var listed = await rig.Commands.RunAsync(
            new LinkCommandRequest("link", ["list"], new Dictionary<string, string>(), context, rig.Lookup, ["orchestrator@tm"]),
            CancellationToken.None);

        Assert.True(listed.Ok, listed.Failure?.Message);
        Assert.Empty(listed.Value!.Result.AsObject()["links"].AsArray());
    }

    [Fact]
    public async Task ListWithNoTeamReadsOnlyTheTeamsOfTheContextAndNeverTheOneInAnotherCheckout()
    {
        var (rig, link) = await GrantedAsync();
        var (lookup, _) = WithAnotherTeamOwnedByTheCaller(rig);
        var before = rig.Effects.Ops.Count;

        var listed = await rig.Commands.RunAsync(
            new LinkCommandRequest("link", ["list"], new Dictionary<string, string>(), rig.Context, lookup, ["orchestrator@zz", "orchestrator@tm"]),
            CancellationToken.None);

        Assert.True(listed.Ok, listed.Failure?.Message);
        Assert.Equal([link], listed.Value!.Result.AsObject()["links"].AsArray().Select(l => l.AsObject()["id"].AsString()));
        Assert.Single(rig.Effects.Ops.Skip(before), op => op == "sweep");
    }

    [Fact]
    public async Task ListNamingATeamKeptInAnotherCheckoutIsRefusedWithTheRerunHint()
    {
        var rig = new Rig();
        var (lookup, otherDir) = WithAnotherTeamOwnedByTheCaller(rig);
        var before = rig.Effects.Ops.Count;

        var listed = await rig.Commands.RunAsync(
            new LinkCommandRequest("link", ["list"], new Dictionary<string, string> { ["team"] = "zz" }, rig.Context, lookup, ["orchestrator@zz", "orchestrator@tm"]),
            CancellationToken.None);

        Assert.False(listed.Ok);
        Assert.Equal($"team zz lives in {otherDir}, outside this --cwd's hierarchy; re-run with --cwd {otherDir}", listed.Failure!.Message);
        Assert.DoesNotContain(rig.Effects.Ops.Skip(before), op => op == "sweep");
    }

    [Fact]
    public async Task ListNamingATeamOfThisContextListsIt()
    {
        var (rig, link) = await GrantedAsync();

        var listed = await rig.Run("orchestrator@tm", "link", ["list"], ("team", "tm"));

        Assert.True(listed.Ok, listed.Failure?.Message);
        Assert.StartsWith($"{link}  active", listed.Value!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListIsTheOrchestratorsAndShowsEachLinkOnALine()
    {
        var (rig, link) = await GrantedAsync();

        var member = await rig.Run("imp@tm", "link", ["list"]);
        var listed = await rig.Run("orchestrator@tm", "link", ["list"]);

        Assert.Equal("orchestrator_only", member.Failure!.Code);
        Assert.True(listed.Ok, listed.Failure?.Message);
        Assert.StartsWith($"{link}  active  task {TaskId}  imp@tm <-> rev@tm  used 0/12  120 min", listed.Value!.Text, StringComparison.Ordinal);
        Assert.Equal("tm", listed.Value.Result.AsObject()["links"].AsArray()[0].AsObject()["team"].AsString());
    }

    [Fact]
    public async Task ARevokedLinkIsNotActiveAgain()
    {
        var (rig, link) = await GrantedAsync();

        var first = await rig.Run("orchestrator@tm", "link", ["revoke", link]);
        var second = await rig.Run("orchestrator@tm", "link", ["revoke", link]);
        var member = await rig.Run("imp@tm", "link", ["revoke", link]);

        Assert.Equal($"revoked {link}", first.Value!.Text);
        Assert.Equal($"{link} is not active", second.Value!.Text);
        Assert.Equal("orchestrator_only", member.Failure!.Code);
        Assert.Contains(rig.Effects.Notices, n => n.Event == "link-ended" && n.Text.StartsWith(link + " revoked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APeerCanRequestAnExtensionOnceAndTheOrchestratorGrantsIt()
    {
        var (rig, link) = await GrantedAsync();

        var first = await rig.Run("imp@tm", "link", ["extend-request", link], ("why", "more time"));
        var second = await rig.Run("rev@tm", "link", ["extend-request", link], ("why", "more time"));

        Assert.True(first.Ok, first.Failure?.Message);
        var extId = first.Value!.Text;
        Assert.Equal($"{extId} (already pending)", second.Value!.Text);
        Assert.Contains(rig.Effects.Notices, n => n.Event == "link-extension" && n.To == "orchestrator@tm");

        var granted = await rig.Run("orchestrator@tm", "link", ["extend", extId], ("messages", "4"));

        Assert.True(granted.Ok, granted.Failure?.Message);
        Assert.Equal($"extended {link} by 4 messages and 30 minutes", granted.Value!.Text);
    }

    // The link's record as the links domain stored it.
    private static JsonObject LinkRecord(Rig rig, string link)
    {
        var data = JsonText.Parse(TestLinks.Text(rig.Fixture.Environment.AhHome, rig.Dir, Team)!).AsObject();
        return data.Get("links")!.Value.AsArray().Single(l => l.AsObject().Get("id")?.AsString() == link).AsObject();
    }

    [Fact]
    public async Task AnExtensionGrantedAfterARevokeIsRefusedAndTheLinkStaysRevoked()
    {
        var (rig, link) = await GrantedAsync();
        var asked = await rig.Run("imp@tm", "link", ["extend-request", link], ("why", "more time"));
        Assert.True(asked.Ok, asked.Failure?.Message);
        var revoked = await rig.Run("orchestrator@tm", "link", ["revoke", link]);
        Assert.Equal($"revoked {link}", revoked.Value!.Text);

        var granted = await rig.Run("orchestrator@tm", "link", ["extend", asked.Value!.Text], ("messages", "4"));

        Assert.Equal("not_extendable", granted.Failure!.Code);
        Assert.Equal("revoked", LinkRecord(rig, link).Get("state")?.AsString());
    }

    [Fact]
    public async Task ALinkEndedByItsMessageCapIsActiveAgainOnceExtended()
    {
        var (rig, link) = await GrantedAsync();
        var data = JsonText.Parse(TestLinks.Text(rig.Fixture.Environment.AhHome, rig.Dir, Team)!).AsObject();
        var record = data.Get("links")!.Value.AsArray().Single(l => l.AsObject().Get("id")?.AsString() == link).AsObject();
        record["state"] = "expired";
        record["end_reason"] = "message_cap";
        record["ended_at"] = Start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        record["used"] = record.Get("limits")!.Value.Member("messages")!.Value;
        TestLinks.Put(rig.Fixture.Environment.AhHome, rig.Dir, Team, JsonText.Stringify(data, 0));

        var asked = await rig.Run("imp@tm", "link", ["extend-request", link], ("why", "cap reached"));
        Assert.True(asked.Ok, asked.Failure?.Message);
        var granted = await rig.Run("orchestrator@tm", "link", ["extend", asked.Value!.Text], ("messages", "4"));

        Assert.True(granted.Ok, granted.Failure?.Message);
        var after = LinkRecord(rig, link);
        Assert.Equal("active", after.Get("state")?.AsString());
        Assert.Null(after.Get("end_reason"));
        Assert.Null(after.Get("ended_at"));
    }

    [Fact]
    public async Task AnExtensionRequestNeedsAPeerAndAReason()
    {
        var (rig, link) = await GrantedAsync();

        var stranger = await rig.Run("arc@tm", "link", ["extend-request", link], ("why", "x"));
        var noWhy = await rig.Run("imp@tm", "link", ["extend-request", link]);
        var unknown = await rig.Run("imp@tm", "link", ["extend-request", "lk-nope"], ("why", "x"));
        var orchestrator = await rig.Run("orchestrator@tm", "link", ["extend-request", link], ("why", "x"));

        Assert.Equal("not_a_peer", stranger.Failure!.Code);
        Assert.Equal("bad_request", noWhy.Failure!.Code);
        Assert.Equal("unknown_link", unknown.Failure!.Code);
        Assert.Equal("not_a_peer", orchestrator.Failure!.Code);
        Assert.DoesNotContain(rig.Requests(), f => f.Contains("--lext-", StringComparison.Ordinal));
    }
}
