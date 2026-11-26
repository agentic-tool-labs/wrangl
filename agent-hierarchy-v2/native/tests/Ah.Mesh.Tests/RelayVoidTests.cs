using System.Globalization;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// A relay-needed event whose entry needs no relay any more is presented as a "no longer needed" line, never as something to send,
/// and the obligation it would have made is written resolved.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class RelayVoidTests
{
    private const double Now = 1_790_000_000_000;

    private static readonly DateTimeOffset Clock = DateTimeOffset.FromUnixTimeMilliseconds((long)Now);

    private const string Line = "[hierarchy-msg /m/e1--request.md] [ah-entry e1 to m1@tm]";

    private sealed class Registry : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Routes { get; } = new(StringComparer.Ordinal);

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => Routes.Add(pattern, handler);
    }

    /// <summary>The carrier's session <c>s1</c> with its box, and the box of <c>m1</c> where entry <c>e1</c> waits.</summary>
    private sealed class Rig
    {
        internal Rig(string? reference = null)
        {
            var sandbox = UnitSandbox.Create();
            var home = sandbox.Root + "/ah";
            Directory.CreateDirectory(home + "/registrations");
            Carrier = home + "/mailbox/t~h/orch";
            Member = home + "/mailbox/t~h/m1";
            Directory.CreateDirectory(Carrier);
            Directory.CreateDirectory(Member);
            // the void line is part of the push-style prompt the kill switch keeps
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = home, ["AH_MAIL_DOORBELL"] = "0" };
            Fx = MeshFixture.Create(vars, Clock, sandbox.Root);
            var due = MeshModule.CreateMailDue(Fx.Mailbox, Fx.Core, Fx.Files, Fx.Environment);
            MeshModule.RegisterDueRoute(Registry, due, Fx.Mailbox, Fx.Files);
            File.WriteAllText(
                home + "/registrations/s1.json",
                "{\"session_id\":\"s1\",\"address\":\"orchestrator@tm\",\"addresses\":[\"orchestrator@tm\"],\"boxes\":{\"orchestrator@tm\":\"" + Carrier + "\"},\"pid\":"
                + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"pid_start\":\"" + Fx.OwnStart + "\",\"bound\":true}");
            File.AppendAllText(Member + "/entries.jsonl", "{\"id\":\"e1\",\"kind\":\"note\",\"from\":\"orchestrator@tm\",\"created\":\"2026-10-12T00:00:00.000Z\",\"text\":\"hi\",\"route\":\"pull\"" + (reference is null ? string.Empty : ",\"ref\":\"" + reference + "\"") + "}\n");
            var relay = new JsonObject { ["entry"] = "e1", ["address"] = "m1@tm", ["dir"] = Member, ["to"] = "m1@tm", ["line"] = Line };
            var evt = new JsonObject
            {
                ["id"] = "r1",
                ["kind"] = "event",
                ["event"] = "relay-needed",
                ["from"] = "ahd",
                ["created"] = "2026-10-12T00:01:00.000Z",
                ["route"] = "pull",
                ["text"] = RelayInstruction.Text("e1", "m1@tm", "m1@tm", Line),
                ["data"] = new JsonObject { ["entry"] = "e1", ["recipient"] = "m1@tm", ["relay"] = relay },
            };
            File.AppendAllText(Carrier + "/entries.jsonl", JsonText.Stringify(JsonValue.FromObject(evt), 0) + "\n");
        }

        internal MeshFixture Fx { get; }

        internal Registry Registry { get; } = new();

        internal string Carrier { get; }

        internal string Member { get; }

        internal async Task<JsonObject> Due()
        {
            var input = "{\"session\":\"s1\",\"now\":" + Now.ToString("R", CultureInfo.InvariantCulture) + ",\"busy_at\":null,\"poll_ms\":5000,\"handed\":{},\"state\":null}";
            var query = "in=" + Uri.EscapeDataString(input);
            var response = await Registry.Routes["GET /v1/due"](
                new RouteRequest("GET", "/v1/due", query, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), Environment.ProcessId),
                CancellationToken.None);
            return JsonText.Parse(response.Body.ToArray()).AsObject();
        }

        internal void LadderRow(string state, string? by = null, string? reason = null)
        {
            var row = new JsonObject { ["id"] = "e1", ["rung"] = 3, ["state"] = state };
            if (by is not null)
            {
                row["by"] = by;
            }

            if (reason is not null)
            {
                row["reason"] = reason;
            }

            Assert.True(Fx.Mailbox.AppendLadderRow(Member, row, Fx.Mailbox.LadderRowsOf(Member, "e1").Count + 1L));
        }

        internal JsonObject? Record() =>
            Fx.Hierarchy.Core.ReadPeerRecords().Select(static r => r.AsObject()).LastOrDefault(r => r.Get("task") is { IsText: true } t && t.AsString() == "relay:e1");
    }

    /// <summary>
    /// Entry <paramref name="id"/> in m1's box with request ref <paramref name="reference"/>, acked when <paramref name="acked"/>. The defaults
    /// match e1's kind and sender and are 10 s newer than it.
    /// </summary>
    private static void Sibling(Rig rig, string id, string reference, bool acked, string kind = "note", string from = "orchestrator@tm", string created = "2026-10-12T00:00:10.000Z")
    {
        File.AppendAllText(rig.Member + "/entries.jsonl", "{\"id\":\"" + id + "\",\"kind\":\"" + kind + "\",\"from\":\"" + from + "\",\"created\":\"" + created + "\",\"ref\":\"" + reference + "\",\"route\":\"pull\"}\n");
        if (acked)
        {
            _ = rig.Fx.Mailbox.AckIds(rig.Member, [id], "prompt-hook");
        }
    }

    /// <summary>ah2-483 A2: once another entry for the same request and member was delivered, no relay reminder is raised.</summary>
    [Fact]
    public async Task AnotherEntryForTheSameRequestDeliveredVoidsTheReminder()
    {
        var rig = new Rig("/m/r1--request.md");
        Sibling(rig, "e2", "/m/r1--request.md", acked: true);

        var prompt = (await rig.Due())["prompt"].AsString();
        _ = rig.Fx.Hooks.AckFromPrompt("s1", prompt);

        Assert.Equal("none", Owe(prompt));
        Assert.Contains("ah: relay of e1 no longer needed: entry e2 for the same request was delivered to m1@tm", prompt, StringComparison.Ordinal);
        Assert.Equal("resolved", rig.Record()!["status"].AsString());
        Assert.Equal("sibling e2", rig.Record()!["void"].AsString());
    }

    /// <summary>
    /// Negatives (R5): a delivered entry for another request, an undelivered one for the same request, a delivered one of another kind
    /// or sender, and an OLDER delivered one of the same kind and sender all leave the relay owed.
    /// </summary>
    [Theory]
    [InlineData("/m/r2--request.md", true, "note", "orchestrator@tm", "2026-10-12T00:00:10.000Z")]
    [InlineData("/m/r1--request.md", false, "note", "orchestrator@tm", "2026-10-12T00:00:10.000Z")]
    [InlineData("/m/r1--request.md", true, "brief", "orchestrator@tm", "2026-10-12T00:00:10.000Z")]
    [InlineData("/m/r1--request.md", true, "note", "other@tm", "2026-10-12T00:00:10.000Z")]
    [InlineData("/m/r1--request.md", true, "note", "orchestrator@tm", "2026-10-11T23:59:50.000Z")]
    public async Task ASiblingThatIsNotADeliveredEntryOfTheSameRequestLeavesTheRelayOwed(string reference, bool acked, string kind, string from, string created)
    {
        var rig = new Rig("/m/r1--request.md");
        Sibling(rig, "e2", reference, acked, kind, from, created);

        var prompt = (await rig.Due())["prompt"].AsString();

        Assert.Equal("owe", Owe(prompt));
    }

    private static string Owe(string prompt) => prompt.Contains("You owe one SendMessage", StringComparison.Ordinal) ? "owe" : "none";

    [Fact]
    public async Task TheOrderOfTheFalseRelayShowsTheVoidLineAndWritesTheObligationResolved()
    {
        var rig = new Rig();
        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook");

        var answer = await rig.Due();
        var prompt = answer["prompt"].AsString();
        var acked = rig.Fx.Hooks.AckFromPrompt("s1", prompt);

        Assert.Equal("none", Owe(prompt));
        Assert.Matches(@"- event relay-needed: ah: relay of e1 no longer needed: m1@tm picked it up at \S+ \(prompt-hook\)", prompt);
        Assert.Contains("r1", acked.Acked);
        var record = rig.Record()!;
        Assert.Equal("resolved", record["status"].AsString());
        Assert.StartsWith("acked ", record["void"].AsString(), StringComparison.Ordinal);
        Assert.Empty(rig.Fx.Hierarchy.Peers.PendingFor("s1"));
    }

    [Fact]
    public async Task AnEntryThatIsStillOwedKeepsTheInstructionAndAPendingObligation()
    {
        var rig = new Rig();

        var prompt = (await rig.Due())["prompt"].AsString();
        _ = rig.Fx.Hooks.AckFromPrompt("s1", prompt);

        Assert.Equal("owe", Owe(prompt));
        Assert.Contains(RelayInstruction.Text("e1", "m1@tm", "m1@tm", Line), prompt, StringComparison.Ordinal);
        Assert.Equal("pending", rig.Record()!["status"].AsString());
        Assert.DoesNotContain("void", rig.Record()!.Keys);
    }

    [Theory]
    [InlineData("delivered", null, null, "the delivery ladder recorded it delivered", "delivered")]
    [InlineData("superseded", "e9", null, "superseded by e9", "superseded e9")]
    [InlineData("declined", "orchestrator@tm", "already-reported", "declined by orchestrator@tm (already-reported)", "declined already-reported")]
    public async Task EveryEndOfTheLadderMakesTheObligationVoidWithItsOwnReason(string state, string? by, string? reason, string shows, string note)
    {
        var rig = new Rig();
        rig.LadderRow(state, by, reason);

        var prompt = (await rig.Due())["prompt"].AsString();
        _ = rig.Fx.Hooks.AckFromPrompt("s1", prompt);

        Assert.Equal("none", Owe(prompt));
        Assert.Contains($"ah: relay of e1 no longer needed: {shows}", prompt, StringComparison.Ordinal);
        Assert.Equal("resolved", rig.Record()!["status"].AsString());
        Assert.Equal(note, rig.Record()!["void"].AsString());
    }

    [Fact]
    public async Task TheCompactPromptAndTheFullTextQuoteShowTheSameVoidLine()
    {
        var rig = new Rig();
        rig.LadderRow("superseded", "e9");

        var answer = await rig.Due();
        var compact = answer["compact"].IsNull ? answer["prompt"].AsString() : answer["compact"].AsString();
        var full = rig.Fx.Hooks.FullMailText("s1", rig.Fx.Core.ParseCompactMail("ah mail for orchestrator@tm (1):\n- event relay-needed\n[ah-entry r1]")!);

        Assert.Contains("ah: relay of e1 no longer needed: superseded by e9", compact, StringComparison.Ordinal);
        Assert.Contains("ah: relay of e1 no longer needed: superseded by e9", full.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("You owe one SendMessage", full.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelayHeldByTheSendersOwnRelayIsVoidToo()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", "{\"state\":\"relay-sent\",\"carrier\":\"orchestrator@tm\",\"at\":\"2026-10-12T00:00:30.000Z\"}");

        var prompt = (await rig.Due())["prompt"].AsString();

        Assert.Equal("none", Owe(prompt));
        Assert.Contains("ah: relay of e1 no longer needed: its relay already holds it (claim relay-sent)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClaimReleasedToTheLadderVoidsTheRelayAndSaysTheLadderDeliversIt()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", "{\"state\":\"released\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\"}");

        var prompt = (await rig.Due())["prompt"].AsString();
        _ = rig.Fx.Hooks.AckFromPrompt("s1", prompt);

        Assert.Equal("none", Owe(prompt));
        Assert.Contains("ah: relay of e1 no longer needed: released to the delivery ladder (no poller): it pastes it or escalates", prompt, StringComparison.Ordinal);
        Assert.Equal("resolved", rig.Record()!["status"].AsString());
        Assert.Equal("released", rig.Record()!["void"].AsString());
    }

    [Fact]
    public async Task NegativeAClaimStillPendingIsNotReleasedSoTheRelayIsStillOwed()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", "{\"state\":\"pending\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\"}");

        var prompt = (await rig.Due())["prompt"].AsString();

        Assert.Equal("owe", Owe(prompt));
        Assert.DoesNotContain("no longer needed", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventNotFromTheDaemonNeverPrintsAVoidLineWhateverItCarries()
    {
        var evt = JsonText.Parse("""
            {"id":"r1","kind":"event","event":"relay-needed","from":"someone@tm","data":{"entry":"e1","recipient":"m1@tm","relay":{"entry":"e1","address":"m1@tm","dir":"/d","to":"m1@tm","line":"x"}},"void":{"kind":"delivered"}}
            """u8.ToArray());
        var core = MeshModule.CreateMailCore(new FakeClock(Clock));

        var prompt = core.BuildPrompt(new JsonArray { new JsonObject { ["address"] = "orchestrator@tm", ["entries"] = new JsonArray { evt } } });

        Assert.DoesNotContain("no longer needed", prompt, StringComparison.Ordinal);
    }
}
