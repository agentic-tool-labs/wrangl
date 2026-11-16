using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>Auto-tracking: a bare SendMessage between the Orchestrator and a team member is filed as a real message-file exchange.</summary>
public sealed class AutoTrackTests
{
    private static readonly string Task = "Please look at the reviewer's second finding on the stream branch and rework the lock so that two parallel spawns cannot both create the tab, then run the Roster tests and report back. " + new string('x', 20);

    private static GateContext Ctx(HooksHostTests.Rig rig, string to, string message, string? agentType = null, string? summary = null, string? agentId = null)
    {
        var input = new JsonObject { ["to"] = to, ["message"] = message };
        if (summary is not null)
        {
            input["summary"] = summary;
        }

        var evt = new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = input,
        };
        if (agentType is not null)
        {
            evt["agent_type"] = agentType;
        }

        if (agentId is not null)
        {
            evt["agent_id"] = agentId;
        }

        return new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(evt, 0))), "x");
    }

    internal const int OwnerPid = 4242;
    internal const string OwnerStart = "Sat Oct 10 09:00:00 2026";

    internal sealed class OwnerTable : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => [OwnerPid, 1];

        public bool? HasAncestor(int pid, int candidate) => candidate is OwnerPid or 1;

        public string? StartTime(long pid) => OwnerStart;
    }

    internal sealed class Live(bool live, bool throws = false) : IMemberLiveness
    {
        public MemberRegistrationLiveness? ForMember(string memberName, string team) =>
            throws ? throw new InvalidOperationException("liveness unreadable") : new MemberRegistrationLiveness("sess", live, "test");

        public bool PidIdentityLive(int pid, string? recordedStart) => live;
    }

    internal static void WriteTeam(HooksHostTests.Rig rig, JsonObject record)
    {
        var project = rig.Sandbox.Root + "/proj";
        Directory.CreateDirectory(project + "/.claude/ah2");
        File.WriteAllText(project + "/.claude/ah2/agent-hierarchy.json", """{"version":1}""");
        var dir = rig.Services.Config.HierarchyDir(project);
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Services.Roster.TeamPath(dir, "tm"))!);
        File.WriteAllBytes(rig.Services.Roster.TeamPath(dir, "tm"), JsonText.ToUtf8(record, 0));
    }

    /// <summary>A rig whose session s-a owns team tm by the process proof the config uses, and whose members are live or not.</summary>
    internal static HooksHostTests.Rig OwnerRig(bool live = true, bool throws = false, bool owns = true, Dictionary<string, string>? env = null, string? pluginRoot = null)
    {
        var rig = new HooksHostTests.Rig(env, processTable: new OwnerTable(), pluginRoot: pluginRoot);
        WriteTeam(rig, new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = owns ? OwnerPid : 99, ["pid_start"] = OwnerStart, ["session_id"] = "s-a" }, ["members"] = new JsonArray() });
        rig.Services.Hierarchy.Core.Liveness = new Live(live, throws);
        return rig;
    }

    /// <summary>A rig for the member session in pane <paramref name="pane"/> of team tm, which holds implementor-1 (p1) and implementor-2 (p2).</summary>
    internal static HooksHostTests.Rig MemberRig(string pane, Ah.Roster.SessionProofs? proofs = null, Action<HooksHostTests.Rig>? before = null)
    {
        var rig = proofs is null
            ? new HooksHostTests.Rig(new Dictionary<string, string> { ["HERDR_PANE_ID"] = pane })
            : new HooksHostTests.Rig(new Dictionary<string, string> { ["HERDR_PANE_ID"] = pane }, processTable: new HarnessSessions().Table, proofs: proofs);
        before?.Invoke(rig);
        WriteTeam(
            rig,
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["session_id"] = "orch" },
                ["members"] = new JsonArray
                {
                    new JsonObject { ["name"] = "implementor-1", ["role"] = "implementor", ["transport_id"] = "p1" },
                    new JsonObject { ["name"] = "implementor-2", ["role"] = "implementor", ["transport_id"] = "p2" },
                },
            });
        return rig;
    }

    private static JsonValue CreatedFile(HooksHostTests.Rig rig)
    {
        var args = rig.Writer.Arguments.Last(a => a.Contains("\"content\"", StringComparison.Ordinal));
        return JsonText.Parse(Encoding.UTF8.GetBytes(args));
    }

    private static int Creates(HooksHostTests.Rig rig) => rig.Writer.Arguments.Count(a => a.Contains("\"content\"", StringComparison.Ordinal));

    private static string Msgs(HooksHostTests.Rig rig) => rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/msgs";

    private static string Request(HooksHostTests.Rig rig, string id, string to, string from, string slug, bool answered = false, string toName = "implementor-1", string team = "tm")
    {
        var dir = Msgs(rig);
        Directory.CreateDirectory(dir);
        var path = $"{dir}/{id}--{to}--{slug}--request.md";
        File.WriteAllText(
            path,
            $"---\nid: {id}\ntype: request\nto: {to}\nfrom: {from}\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: {toName}\nfrom_name: null\nteam: {team}\ncreated: 2026-10-11T11:00:00+00:00\n---\n\n## [0] tldr\n- do the thing\n");
        if (answered)
        {
            File.WriteAllText(
                $"{dir}/{id}--{from}--{slug}--response.md",
                $"---\nid: {id}\ntype: response\nto: {from}\nfrom: {to}\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: null\ncreated: 2026-10-11T11:30:00+00:00\n---\n\n## [0] tldr\n- done\n");
        }

        return path;
    }

    private static void SeedPeerRows(HooksHostTests.Rig rig, params string[] rows)
    {
        var file = Ah.Core.Text.AhPaths.UserEntry(rig.Sandbox.Home, Ah.Core.Text.AhPaths.PeerPendingFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", rows) + "\n");
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim()))).ToLowerInvariant();

    [Theory]
    [InlineData("Please review the lock change!", "please-review-the-lock-change")]
    [InlineData("  --Ünïcode & späce__ ", "n-code-sp-ce")]
    [InlineData("!!!", "msg")]
    [InlineData("pipeline-run-anchor", "pipeline-run-anchor-msg")]
    [InlineData("rework the stream lock and the doc", "rework-the-stream-lock-and-the")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghij", "abcdefghijklmnopqrstuvwxyzabcdef")]
    public void ASlugIsLowercaseDashedAtMost32CharactersCutAtADash(string source, string expected)
    {
        var slug = AutoTrack.SlugOf(source, new HashSet<string>());

        Assert.Equal(expected, slug);
        Assert.True(slug.Length <= 32);
    }

    [Fact]
    public void ACollidingSlugGetsANumberedSuffixWithinTheLimit()
    {
        var taken = new HashSet<string> { "rework-the-stream-lock-and-the", "rework-the-stream-lock-and-the-2" };

        Assert.Equal("rework-the-stream-lock-and-the-3", AutoTrack.SlugOf("rework the stream lock and the doc", taken));
        Assert.Equal("abcdefghijklmnopqrstuvwxyzabcd-2", AutoTrack.SlugOf("abcdefghijklmnopqrstuvwxyzabcdefghij", new HashSet<string> { "abcdefghijklmnopqrstuvwxyzabcdef" }));
    }

    [Theory]
    [InlineData("go")]
    [InlineData("proceed.")]
    [InlineData("Continue")]
    [InlineData("okay do the next one")]
    [InlineData("merged; stand by")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AnOrchestratorMessageToALiveMemberIsARequestWhateverItsLength(string text)
    {
        var plan = AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", text), out _);

        Assert.Equal("implementor", plan!.Role);
        Assert.Null(plan.RequestPath);
    }

    /// <summary>A rig whose session s-a owns the child team tm (parent mgr) and is also the member in pane p9 of mgr.</summary>
    private static HooksHostTests.Rig SubOrchestratorRig()
    {
        var rig = new HooksHostTests.Rig(new Dictionary<string, string> { ["HERDR_PANE_ID"] = "p9" }, processTable: new OwnerTable());
        WriteTeam(rig, new JsonObject
        {
            ["orchestrator"] = new JsonObject { ["pid"] = OwnerPid, ["pid_start"] = OwnerStart, ["session_id"] = "s-a" },
            ["members"] = new JsonArray(),
            ["parent"] = new JsonObject { ["team"] = "mgr" },
        });
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        File.WriteAllBytes(
            rig.Services.Roster.TeamPath(dir, "mgr"),
            JsonText.ToUtf8(
                new JsonObject
                {
                    ["orchestrator"] = new JsonObject { ["session_id"] = "top" },
                    ["members"] = new JsonArray { new JsonObject { ["name"] = "sub-orchestrator-1", ["role"] = "sub-orchestrator", ["transport_id"] = "p9" } },
                },
                0));
        rig.Services.Hierarchy.Core.Liveness = new Live(true);
        return rig;
    }

    [Fact]
    public void ASubOrchestratorsMessageToItsOwnLaneMemberIsFiledByTheOwnerPathNotTheMemberReplyPath()
    {
        var plan = AutoTrack.Classify(Ctx(SubOrchestratorRig(), "tm-implementor", "run the next slice"), out _);

        Assert.NotNull(plan);
        Assert.Equal("implementor", plan.Role);
        Assert.Equal("tm", plan.Team);
        Assert.Null(plan.RequestPath);
    }

    [Fact]
    public void ASubOrchestratorsDispatchToItsLaneMemberIsARequestWithAnIdAndAnEtaInTheChildTeam()
    {
        var rig = SubOrchestratorRig();
        var c = Ctx(rig, "tm-implementor", Task, summary: "Rework the stream lock");

        var (path, advice) = AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

        var content = CreatedFile(rig).Text("content")!;
        Assert.EndsWith("--rework-the-stream-lock--request.md", path, StringComparison.Ordinal);
        Assert.Matches(@"\nid: \d{8}-\d{6}-[0-9a-z]{4}\n", content);
        Assert.Contains("\nreason: plain-send\n", content, StringComparison.Ordinal);
        Assert.Contains("\neta: small\n", content, StringComparison.Ordinal);
        Assert.Contains("\nteam: tm\n", content, StringComparison.Ordinal);
        Assert.Contains("\nto_name: tm-implementor\n", content, StringComparison.Ordinal);
        Assert.Null(advice);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("thanks")]
    [InlineData("Got it")]
    [InlineData("👍")]
    [InlineData("ok.")]
    [InlineData("thanks!")]
    [InlineData("yes")]
    [InlineData("Will do.")]
    public void APureAcknowledgementIsNotFiled(string text) =>
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", text), out _));

    [Theory]
    [InlineData("ping")]
    [InlineData("hello")]
    [InlineData(" PING ")]
    [InlineData("Hello!")]
    [InlineData("hey?")]
    [InlineData("pong")]
    [InlineData("hi.")]
    public void AGreetingToAMemberWithOrWithoutALiveSessionIsLeftBare(string text)
    {
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(live: false), "tm-implementor", text), out _));
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", text), out _));
    }

    [Theory]
    [InlineData("ping the reviewer")]
    [InlineData("hello, start on X")]
    [InlineData("hi there")]
    [InlineData("pinged")]
    [InlineData("continue")]
    public void AGreetingWordInsideALongerMessageOrAnInstructionWordIsStillFiled(string text) =>
        Assert.NotNull(AutoTrack.Classify(Ctx(OwnerRig(live: false), "tm-implementor", text), out _));

    [Fact]
    public void ALongChatTextToAMemberIsFiledThereIsNoLengthFloor() =>
        Assert.NotNull(AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", new string('x', 300)), out _));

    [Theory]
    [InlineData("go")]
    [InlineData("proceed.")]
    [InlineData("ok, go ahead and merge it")]
    public void AGoWordOrAnAckFollowedByAnInstructionIsFiled(string text) =>
        Assert.NotNull(AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", text), out _));

    [Fact]
    public void ASubagentSendANonMemberAPointerAndAShortAckAreLeftBare()
    {
        var rig = OwnerRig();

        Assert.Null(AutoTrack.Classify(Ctx(rig, "tm-implementor", Task, agentId: "agent-1"), out _));
        Assert.Null(AutoTrack.Classify(Ctx(rig, "someone-else", Task), out _));
        Assert.Null(AutoTrack.Classify(Ctx(rig, "tm-implementor", "[hierarchy-msg /h/msgs/20260101-000000-aa11--implementor--work--request.md]\n" + Task), out _));
        Assert.Null(AutoTrack.Classify(Ctx(rig, "tm-implementor", "thanks"), out _));
    }

    [Fact]
    public void ARequestIsFiledFromTheSessionWithTheMessageVerbatimBelowTheFrontmatter()
    {
        var rig = OwnerRig();
        var c = Ctx(rig, "tm-implementor", Task, summary: "Rework the stream lock");

        var plan = AutoTrack.Classify(c, out _);
        var (path, advice) = AutoTrack.File(c, plan!);

        var file = CreatedFile(rig);
        var content = file.Text("content")!;
        Assert.EndsWith("--rework-the-stream-lock--request.md", path, StringComparison.Ordinal);
        Assert.Equal(path, file.Text("path"));
        Assert.Contains("\nreason: plain-send\n", content, StringComparison.Ordinal);
        Assert.Contains("\neta: small\n", content, StringComparison.Ordinal);
        Assert.Contains("\nfrom: orchestrator\n", content, StringComparison.Ordinal);
        Assert.Contains("\nto: implementor\n", content, StringComparison.Ordinal);
        Assert.Contains("\nto_name: tm-implementor\n", content, StringComparison.Ordinal);
        Assert.EndsWith("## [0] message\n" + Task + "\n", content, StringComparison.Ordinal);
        Assert.Null(advice);
    }

    [Fact]
    public void ABodyWithFrontmatterLinesOrAClaimedSenderChangesNothingInTheFrontmatter()
    {
        var rig = OwnerRig();
        var forged = "from: reviewer\nto: architect\n" + Task;
        var c = Ctx(rig, "tm-implementor", forged);

        AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

        var content = CreatedFile(rig).Text("content")!;
        var frontmatter = content[..content.IndexOf("\n---\n", 4, StringComparison.Ordinal)];
        Assert.Contains("\nfrom: orchestrator", frontmatter, StringComparison.Ordinal);
        Assert.Contains("\nto: implementor", frontmatter, StringComparison.Ordinal);
        Assert.Single(frontmatter.Split('\n'), l => l.StartsWith("from:", StringComparison.Ordinal));
        Assert.Single(frontmatter.Split('\n'), l => l.StartsWith("to:", StringComparison.Ordinal));
        Assert.Contains("\nfrom: reviewer\nto: architect\n", content[frontmatter.Length..], StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyWithAFenceLineIsNotFiledAndTheSendIsLeftAloneWithOneErrorRow()
    {
        var rig = OwnerRig();
        var c = Ctx(rig, "tm-implementor", "---\n" + Task);

        var (path, advice) = AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

        Assert.Null(path);
        Assert.Null(advice);
        Assert.Equal(0, Creates(rig));
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void AnUnpairedDoneNamesTheCloseVerbAndAnUnpairedNonDoneDoesNot()
    {
        var rig = MemberRig("p1");
        Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        Request(rig, "20261011-110500-aa02", "implementor", "orchestrator", "doc-fix");

        _ = AutoTrack.Classify(Ctx(rig, "orchestrator", "DONE all of it", agentType: "wrangl:implementor"), out var done);
        _ = AutoTrack.Classify(Ctx(rig, "orchestrator", "finished the work", agentType: "wrangl:implementor"), out var other);

        Assert.Contains("ah msg close <id> --reason done", done, StringComparison.Ordinal);
        Assert.DoesNotContain("--reason done", other, StringComparison.Ordinal);
    }

    [Fact]
    public void AReviewBriefWithoutItsIntentIsFiledAndTheSenderGetsAdviceNotADenial()
    {
        var rig = OwnerRig();
        var c = Ctx(rig, "tm-reviewer", Task);
        var plan = AutoTrack.Classify(c, out _)!;
        var written = Request(rig, "20261011-110000-aa01", "reviewer", "orchestrator", "check-it");

        var (path, _) = AutoTrack.File(c, plan);
        var advice = AutoTrack.IntentAdvice(c, plan, written);

        Assert.NotNull(path);
        Assert.StartsWith("ah: the auto-filed brief 20261011-110000-aa01 has no ", advice, StringComparison.Ordinal);
        Assert.Contains("`ah msg fill --id 20261011-110000-aa01 --from <your role> --request --section <1 or 5> --body-file -`", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameBodyWithinTenMinutesReusesTheRequestAndAfterTenMinutesMakesANewOne()
    {
        var rig = OwnerRig();
        var c = Ctx(rig, "tm-implementor", Task);
        var plan = AutoTrack.Classify(c, out _)!;
        var hash = HashOf(Task);
        SeedPeerRows(rig, $"{{\"type\":\"auto-track\",\"session_id\":\"s-a\",\"to\":\"tm-implementor\",\"hash\":\"{hash}\",\"path\":\"/held/request.md\",\"ts\":\"2026-10-11T11:55:00.000Z\"}}");

        var reused = AutoTrack.File(c, plan);

        Assert.Equal("/held/request.md", reused.Path);
        Assert.Equal(0, Creates(rig));

        SeedPeerRows(rig, $"{{\"type\":\"auto-track\",\"session_id\":\"s-a\",\"to\":\"tm-implementor\",\"hash\":\"{hash}\",\"path\":\"/held/request.md\",\"ts\":\"2026-10-11T11:49:00.000Z\"}}");

        var fresh = AutoTrack.File(c, plan);

        Assert.NotEqual("/held/request.md", fresh.Path);
        Assert.Equal(1, Creates(rig));
    }

    [Fact]
    public void AMemberReplyToTheOneOpenRequestFromTheOrchestratorIsFiledAsItsResponse()
    {
        var rig = MemberRig("p1");
        var request = Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        var c = Ctx(rig, "orchestrator", "DONE lock-rework: the lock is in", agentType: "wrangl:implementor");

        var plan = AutoTrack.Classify(c, out _);
        var (path, _) = AutoTrack.File(c, plan!);

        Assert.Equal(request, plan!.RequestPath);
        Assert.Equal("implementor", plan.Role);
        Assert.EndsWith("--response.md", path, StringComparison.Ordinal);
        Assert.EndsWith("## [0] message\nDONE lock-rework: the lock is in\n", CreatedFile(rig).Text("content"), StringComparison.Ordinal);
    }

    private static readonly string[] PlanEvents = ["add", "next"];

    private static string LinkedRequest(HooksHostTests.Rig rig, string item)
    {
        var path = Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        var text = File.ReadAllText(path).Replace("team: tm\n", $"team: tm\nteam_file: {rig.Sandbox.Root}/proj/teams/tm.json\nitem: {item}\n", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        var progress = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/progress/tm";
        Directory.CreateDirectory(progress);
        File.WriteAllText(
            progress + "/_run.plan.jsonl",
            string.Join('\n', PlanEvents.Select(e => $"{{\"v\":1,\"ts\":\"2026-10-11T11:00:00Z\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{e}\"{(e == "add" ? ",\"title\":\"Do it\"" : string.Empty)},\"by\":\"o\",\"session\":null}}")) + "\n");
        Ah.TestKit.TestImport.Planted(rig.Services.Environment.AhHome, [progress + "/_run.plan.jsonl"]);
        return path;
    }

    [Fact]
    public void AMemberReplyPairedToALinkedRequestSetsTheItemWaitingOnTheOrchestratorAndAnUnlinkedOneDoesNot()
    {
        var linked = MemberRig("p1");
        _ = LinkedRequest(linked, "stab");
        var c = Ctx(linked, "orchestrator", "STATUS lock-rework: halfway", agentType: "wrangl:implementor");
        _ = AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

        var unlinked = MemberRig("p1");
        _ = Request(unlinked, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        var u = Ctx(unlinked, "orchestrator", "STATUS lock-rework: halfway", agentType: "wrangl:implementor");
        _ = AutoTrack.File(u, AutoTrack.Classify(u, out _)!);

        Assert.Contains(linked.Writer.Arguments, a => a.Contains("wait-orch", StringComparison.Ordinal) && a.Contains("answer implementor-1: STATUS", StringComparison.Ordinal));
        Assert.DoesNotContain(unlinked.Writer.Arguments, a => a.Contains("wait-orch", StringComparison.Ordinal));
    }

    [Fact]
    public void ADoneReplyRecordsHeadOnlyFromACleanTreeAndNegativeAnUntrackedFileRecordsNone()
    {
        static string ReviewRow(bool dirty)
        {
            var rig = MemberRig("p1", before: r =>
            {
                var repo = r.Sandbox.Root + "/proj";
                HooksHostTests.GitIn(repo, "init", "-q", "-b", "main");
                _ = Directory.CreateDirectory(repo + "/.claude/ah2");
                File.WriteAllText(repo + "/.claude/ah2/ah-conventions.json", """{"version":1}""");
                HooksHostTests.GitIn(repo, "add", ".claude/ah2/ah-conventions.json");
                HooksHostTests.GitIn(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
                File.WriteAllText(repo + "/.git/info/exclude", dirty ? "*\n!stray.txt\n" : "*\n");
                if (dirty)
                {
                    File.WriteAllText(repo + "/stray.txt", "x");
                }
            });
            _ = LinkedRequest(rig, "stab");
            var c = Ctx(rig, "orchestrator", "DONE stab: built", agentType: "wrangl:implementor");

            _ = AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

            return Assert.Single(rig.Writer.Arguments, static a => a.Contains("\"event\":\"report\"", StringComparison.Ordinal));
        }

        Assert.Contains("\"commits\"", ReviewRow(dirty: false), StringComparison.Ordinal);
        Assert.DoesNotContain("\"commits\"", ReviewRow(dirty: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ADoneReplyPairedToALinkedRequestGoesThroughTheTransitionTableToReported()
    {
        var rig = MemberRig("p1");
        _ = LinkedRequest(rig, "stab");
        var c = Ctx(rig, "orchestrator", "DONE stab: built", agentType: "wrangl:implementor");

        _ = AutoTrack.File(c, AutoTrack.Classify(c, out _)!);

        Assert.Contains(rig.Writer.Arguments, a => a.Contains("\"event\":\"report\"", StringComparison.Ordinal) && a.Contains("\"cause\"", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Writer.Arguments, a => a.Contains("wait-orch", StringComparison.Ordinal));
    }

    [Fact]
    public void WithTwoOpenRequestsTheSlugInTheFirstLinePicksOneAndNoSlugLeavesTheReplyBareWithAdvice()
    {
        var rig = MemberRig("p1");
        var first = Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        Request(rig, "20261011-110500-aa02", "implementor", "orchestrator", "doc-fix");

        var named = AutoTrack.Classify(Ctx(rig, "orchestrator", "DONE lock-rework", agentType: "wrangl:implementor"), out _);
        var unnamed = AutoTrack.Classify(Ctx(rig, "orchestrator", "finished the work", agentType: "wrangl:implementor"), out var context);

        Assert.Equal(first, named!.RequestPath);
        Assert.Null(unnamed);
        Assert.Contains("20261011-110000-aa01 (lock-rework)", context, StringComparison.Ordinal);
        Assert.Contains("20261011-110500-aa02 (doc-fix)", context, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplyIsLeftBareWithoutAnOpenRequestFromTheRecipientOrForAnAckWord()
    {
        var rig = MemberRig("p1");
        Request(rig, "20261011-110000-aa01", "implementor", "reviewer", "other-work");
        Request(rig, "20261011-110100-aa02", "implementor", "orchestrator", "done-work", answered: true);

        Assert.Null(AutoTrack.Classify(Ctx(rig, "orchestrator", "DONE other-work", agentType: "wrangl:implementor"), out _));
        Request(rig, "20261011-110200-aa03", "implementor", "orchestrator", "open-work");
        Assert.Null(AutoTrack.Classify(Ctx(rig, "orchestrator", "thanks", agentType: "wrangl:implementor"), out _));
        Assert.NotNull(AutoTrack.Classify(Ctx(rig, "orchestrator", "thanks for the brief, DONE open-work", agentType: "wrangl:implementor"), out _));
    }

    [Fact]
    public void AClassifiedSendIsNotNudgedByTheMsgGate()
    {
        var rig = OwnerRig();
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = new JsonObject { ["to"] = "tm-implementor", ["message"] = Task, ["notify_when_idle"] = true },
        };

        var outcome = rig.Run("pretooluse-msg-gate", Encoding.UTF8.GetString(JsonText.ToUtf8(evt, 0)), gate: true);

        Assert.True(outcome.Stdout.IsEmpty, Encoding.UTF8.GetString(outcome.Stdout.Span));
    }

    private static string GateOutput(HooksHostTests.Rig rig, string message)
    {
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["agent_type"] = "wrangl:implementor",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = new JsonObject { ["to"] = "orchestrator", ["message"] = message },
        };
        var outcome = rig.Run("pretooluse-sendmessage-response", Encoding.UTF8.GetString(JsonText.ToUtf8(evt, 0)), gate: true);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    [Fact]
    public void TheResponseGateLetsAReplyThatAutoTrackingFilesGoAndBouncesOneItWillNotFile()
    {
        var long_ = "DONE lock-rework\n" + new string('y', 2500);
        var open = MemberRig("p1");
        var request = Request(open, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework");
        SeedPeerRows(open, $"{{\"task\":\"lock\",\"ts\":\"2026-10-11T11:00:00.000Z\",\"session_id\":\"s-a\",\"status\":\"pending\",\"msg\":\"{request}\",\"from\":\"orchestrator\"}}");
        var answered = MemberRig("p1");
        var done = Request(answered, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework", answered: true);
        SeedPeerRows(answered, $"{{\"task\":\"lock\",\"ts\":\"2026-10-11T11:00:00.000Z\",\"session_id\":\"s-a\",\"status\":\"pending\",\"msg\":\"{done}\",\"from\":\"orchestrator\"}}");

        Assert.Equal(string.Empty, GateOutput(open, long_));
        var nudge = GateOutput(answered, long_);
        Assert.Contains("\"permissionDecision\":\"deny\"", nudge, StringComparison.Ordinal);
        Assert.Contains("NOT DONE: you hold an unanswered", nudge, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInlineReportBouncesOnceThenItsIdenticalRetryPassesAndAShortReplyNeverBounces()
    {
        var long_ = "DONE lock-rework\n" + new string('y', 2500);
        var rig = MemberRig("p1");
        rig.Writer.Executing = [.. rig.Writer.Executing ?? [], .. rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "gates")];
        var done = Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework", answered: true);
        SeedPeerRows(rig, $"{{\"task\":\"lock\",\"ts\":\"2026-10-11T11:00:00.000Z\",\"session_id\":\"s-a\",\"status\":\"pending\",\"msg\":\"{done}\",\"from\":\"orchestrator\"}}");

        Assert.Equal(string.Empty, GateOutput(rig, "DONE lock-rework"));
        Assert.Contains("NOT DONE: you hold an unanswered", GateOutput(rig, long_), StringComparison.Ordinal);
        Assert.Equal(string.Empty, GateOutput(rig, long_));
        Assert.Contains("\"outcome\":\"retry-pass\"", rig.GatesText(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj")), StringComparison.Ordinal);
    }

    [Fact]
    public void APlainSendGetsNoFilingNudgeWhateverItsTextBecauseNothingIsFiledForIt()
    {
        var fenced = OwnerRig();
        var plain = OwnerRig();

        Assert.Null(AutoTrack.AfterSend(Ctx(fenced, "tm-implementor", "---\n" + Task)));
        Assert.Null(AutoTrack.AfterSend(Ctx(plain, "tm-implementor", Task)));
        Assert.Equal(0, Creates(fenced));
        Assert.Equal(0, Creates(plain));
    }

    private static GateContext SentCtx(HooksHostTests.Rig rig, string to, string message, JsonObject response)
    {
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = new JsonObject { ["to"] = to, ["message"] = message },
            ["tool_response"] = response,
        };
        return new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(evt, 0))), "x");
    }

    private static string StateOfWaitingItem(HooksHostTests.Rig rig, string message, JsonObject response)
    {
        rig.Writer.Executing = rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "progress").ToList();
        var pool = Ah.Hooks.Lifecycle.ProgressPool.Of(rig.Services, rig.Sandbox.Root + "/proj");
        var p = rig.Services.Hierarchy.Progress;
        Assert.Null(p.RecordChecklist(pool, "tm", "_run", "o@tm", [new PlanInput("a", "add", "Do a"), new PlanInput("a", "next")]).Refused);
        Assert.Null(HierarchyModule.ChecklistOnDispatch(p, pool, "tm", "a", "tm-implementor", "20261022-100000-aaaa", "o@tm", null));
        Assert.Null(HierarchyModule.ChecklistOnReply(p, pool, "tm", "20261022-100000-aaaa", "a", "tm-implementor", "BLOCKED", "tm-implementor@tm", null));

        _ = AutoTrack.AfterSend(SentCtx(rig, "tm-implementor", message, response));

        return p.ReadPlan(pool, "tm", "_run")!.Items.Single(i => i.Item == "a").State;
    }

    [Fact]
    public void AnOrchestratorSendClearsTheMembersWaitingItemOnlyOnceItHasGoneOut()
    {
        var sent = new JsonObject { ["success"] = true, ["msg_id"] = "m-1" };

        Assert.Equal("in_progress", StateOfWaitingItem(OwnerRig(), Task, sent));
        Assert.Equal("waiting_orch", StateOfWaitingItem(OwnerRig(), Task, new JsonObject { ["success"] = false, ["error"] = "denied" }));
        Assert.Equal("waiting_orch", StateOfWaitingItem(OwnerRig(), Task, new JsonObject { ["success"] = true }));
    }

    [Fact]
    public void AReplyFromOneMemberDoesNotFileIntoASameRoleMembersRequestButTheAddresseeDoes()
    {
        var other = MemberRig("p1");
        Request(other, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework", toName: "implementor-2");
        var addressee = MemberRig("p2");
        var request = Request(addressee, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework", toName: "implementor-2");

        Assert.Null(AutoTrack.Classify(Ctx(other, "orchestrator", "DONE lock-rework", agentType: "wrangl:implementor"), out _));
        Assert.Equal(request, AutoTrack.Classify(Ctx(addressee, "orchestrator", "DONE lock-rework", agentType: "wrangl:implementor"), out _)!.RequestPath);
    }

    [Fact]
    public void AReplyDoesNotFileIntoARequestOfAnotherTeamInTheSamePool()
    {
        var rig = MemberRig("p1");
        Request(rig, "20261011-110000-aa01", "implementor", "orchestrator", "lock-rework", team: "other");

        Assert.Null(AutoTrack.Classify(Ctx(rig, "orchestrator", "DONE lock-rework", agentType: "wrangl:implementor"), out _));
    }

    [Fact]
    public void ALongMessageFromASessionThatDoesNotOwnTheTeamFilesNoRequestButADeadMemberIsStillFiledWithoutNotify()
    {
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(owns: false), "tm-implementor", Task), out _));
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(owns: false), "tm-implementor", "go"), out _));
        Assert.Null(AutoTrack.Classify(Ctx(OwnerRig(live: false), "tm-implementor", "thanks"), out _));
        Assert.True(AutoTrack.Classify(Ctx(OwnerRig(), "tm-implementor", Task), out _)!.Live);
        Assert.False(AutoTrack.Classify(Ctx(OwnerRig(live: false), "tm-implementor", Task), out _)!.Live);
    }

    [Fact]
    public void AClassificationThatThrowsLeavesTheSendAloneWithOneErrorRow()
    {
        var rig = OwnerRig(throws: true);
        var c = Ctx(rig, "tm-implementor", Task);

        Assert.Null(AutoTrack.Classify(c, out var context));
        Assert.Null(context);
        Assert.Null(AutoTrack.AfterSend(c));
        Assert.Equal(0, Creates(rig));
        Assert.Equal(2, rig.Errors.Records.Count);
    }
}
