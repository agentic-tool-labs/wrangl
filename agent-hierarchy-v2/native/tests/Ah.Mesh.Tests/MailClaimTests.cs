using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.State;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// The per-entry claim record: the carrier is decided once when the entry is created, and the claim operation is atomic under the
/// box's append lock, so of a sender's relay and the daemon's fallback exactly one wins.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailClaimTests
{
    private const long DeadPid = 4_000_000;

    private static readonly DateTimeOffset Instant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    internal sealed record Rig(MeshFixture Fx, string Hierarchy, string TeamFile, string BoxA, string BoxB)
    {
        internal JsonObject? Send(string id, string from = "a@tm", string to = "b@tm", string? session = "s-a", string? text = null)
        {
            text ??= "hello " + id;
            var request = new JsonObject { ["to"] = to, ["from"] = from, ["id"] = id, ["text"] = text, ["cwd"] = Hierarchy, ["hierarchy_dir"] = Hierarchy };
            if (session is not null)
            {
                request["session_id"] = session;
            }

            var outcome = Fx.Mailbox.Send(request);
            Assert.True(outcome.Ok, outcome.Failure?.Message);
            return outcome.Value;
        }

        internal JsonObject? Claim(string id, string box = "") => Fx.Mailbox.ReadClaim(box.Length == 0 ? BoxB : box, id);
    }

    internal static Rig Create(string? settings = null, long senderPid = -1, long receiverPid = -1, string receiverHarness = "claude", bool registerReceiver = true, bool withPidStart = false, IProcessTable? table = null, Ah.Hierarchy.PeerRecordsRoute? peerRoute = null, Ah.Hierarchy.MsgsRoute? msgsRoute = null, bool doorbell = true, bool withHolds = false)
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        File.WriteAllText(
            hierarchy + "/teams/tm.json",
            "{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n");
        if (settings is not null)
        {
            File.WriteAllText(hierarchy + "/agent-hierarchy.json", settings);
        }

        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        if (!doorbell)
        {
            vars["AH_MAIL_DOORBELL"] = "0";
        }

        var fx = MeshFixture.Create(vars, Instant, sandbox.Root, singleCarrier: true, table: table, peerRoute: peerRoute, msgsRoute: msgsRoute, withHolds: withHolds);
        var team = hierarchy + "/teams/tm.json";
        var boxA = fx.Mailbox.BoxDirFor(team, "a");
        var boxB = fx.Mailbox.BoxDirFor(team, "b");
        var live = Environment.ProcessId;
        var start = withPidStart ? fx.Mailbox.ProcessStart(live) : null;
        Assert.True(fx.Mailbox.WriteRegistration(new RegistrationWrite("s-a", [new BoxRef("a@tm", boxA)], team, "claude", senderPid < 0 ? live : senderPid, PidStart: start)));
        if (registerReceiver)
        {
            Assert.True(fx.Mailbox.WriteRegistration(new RegistrationWrite("s-b", [new BoxRef("b@tm", boxB)], team, receiverHarness, receiverPid < 0 ? live : receiverPid, PidStart: start)));
        }

        return new Rig(fx, hierarchy, team, boxA, boxB);
    }

    [Fact]
    public void AHoldExemptSendIsMarkedInItsEntryAndAnOrdinarySendIsNot()
    {
        var rig = Create();

        var exempt = rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["id"] = "ex", ["text"] = "save", ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy, ["hold_exempt"] = true });
        Assert.True(exempt.Ok, exempt.Failure?.Message);
        rig.Send("plain");

        var entries = rig.Fx.Mailbox.ReadEntries(rig.BoxB).Entries;
        Assert.True(entries.Single(e => e.Member("id")!.Value.AsString() == "ex").Member("hold_exempt")!.Value.AsBool());
        Assert.Null(entries.Single(e => e.Member("id")!.Value.AsString() == "plain").Member("hold_exempt"));
    }

    [Fact]
    public void ALiveClaudeSenderToALiveClaudeReceiverGetsARelayClaimInPending()
    {
        var rig = Create();

        rig.Send("e1");

        var claim = rig.Claim("e1");
        Assert.NotNull(claim);
        Assert.Equal("pending", claim!.Get("state")!.Value.AsString());
        Assert.Equal("relay", claim.Get("carrier")!.Value.AsString());
        Assert.Equal("s-a", claim.Get("sender_session")!.Value.AsString());
        Assert.Equal(180000, claim.Get("l0_ms")!.Value.AsNumber());
        Assert.Equal(60000, claim.Get("l1_ms")!.Value.AsNumber());
    }

    [Fact]
    public void AReceiverWithAFreshPullGetsAPromptClaimAndAStaleOneARelay()
    {
        // doorbell off: a member's pull-routed note would otherwise be a digest note, which gets no claim at all
        var rig = Create(doorbell: false);
        rig.Fx.Mailbox.WriteHeartbeat(rig.BoxB, "claude-mod", "s-b", 5000);

        rig.Send("fresh");
        Assert.Equal("prompt", rig.Claim("fresh")!.Get("carrier")!.Value.AsString());

        rig.Fx.Clock.UtcNow += TimeSpan.FromHours(1);
        rig.Send("stale");
        Assert.Equal("relay", rig.Claim("stale")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void ASendWithNoSessionIsCarrierPrompt()
    {
        var rig = Create();

        rig.Send("e1", from: "user", session: null);

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void ADaemonEventIsCarrierPromptEvenFromALiveSession()
    {
        var rig = Create();

        rig.Send("e1", from: "ahd", session: "s-a");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void ASenderWhoseProcessIsGoneIsCarrierPrompt()
    {
        var rig = Create(senderPid: DeadPid);

        rig.Send("e1");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void ANonClaudeReceiverIsCarrierPrompt()
    {
        var rig = Create(receiverHarness: "codex");

        rig.Send("e1");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void AReceiverWithNoLiveSessionIsCarrierPrompt()
    {
        var rig = Create(registerReceiver: false);

        rig.Send("e1");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void ADeadReceiverProcessIsCarrierPrompt()
    {
        var rig = Create(receiverPid: DeadPid);

        rig.Send("e1");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void MailToOneselfIsCarrierPrompt()
    {
        var rig = Create();

        rig.Send("e1", from: "a@tm", to: "a@tm");

        Assert.Equal("prompt", rig.Claim("e1", rig.BoxA)!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void AFromAddressTheSendersSessionDoesNotHoldIsCarrierPrompt()
    {
        var rig = Create();

        rig.Send("e1", from: "b@tm", to: "a@tm");

        Assert.Equal("prompt", rig.Claim("e1", rig.BoxA)!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void AZeroRelayLeaseMakesEveryEntryCarrierPrompt()
    {
        var rig = Create("{\"relay_lease_ms\":0}\n");

        rig.Send("e1");

        Assert.Equal("prompt", rig.Claim("e1")!.Get("carrier")!.Value.AsString());
    }

    [Fact]
    public void TheLeasesComeFromTheSettingsAndAreCopiedIntoTheRecord()
    {
        var rig = Create("{\"relay_lease_ms\":90000,\"claim_lease_ms\":30000}\n");

        rig.Send("e1");

        var claim = rig.Claim("e1")!;
        Assert.Equal(90000, claim.Get("l0_ms")!.Value.AsNumber());
        Assert.Equal(30000, claim.Get("l1_ms")!.Value.AsNumber());
    }

    [Fact]
    public void ALeaseThatIsNotAWholeNumberIsIgnored()
    {
        var rig = Create("{\"relay_lease_ms\":\"soon\",\"claim_lease_ms\":-5}\n");

        rig.Send("e1");

        var claim = rig.Claim("e1")!;
        Assert.Equal(180000, claim.Get("l0_ms")!.Value.AsNumber());
        Assert.Equal(60000, claim.Get("l1_ms")!.Value.AsNumber());
    }

    [Fact]
    public void WithTheSwitchOffNoClaimRecordIsWritten()
    {
        var rig = Create("{\"single_carrier\":false}\n");

        rig.Send("e1");

        Assert.Null(rig.Claim("e1"));
        Assert.Single(rig.Fx.Mailbox.ReadEntries(rig.BoxB).Entries);
    }

    [Fact]
    public void ASwitchValueThatIsNotABooleanLeavesSingleCarrierOn()
    {
        var rig = Create("{\"single_carrier\":\"false\"}\n");

        rig.Send("e1");

        Assert.NotNull(rig.Claim("e1"));
    }

    [Fact]
    public void ARepeatedSendOfTheSameIdKeepsTheFirstClaim()
    {
        var rig = Create();
        rig.Send("e1");
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-a" });

        rig.Send("e1");

        Assert.Equal("relay-claimed", rig.Claim("e1")!.Get("state")!.Value.AsString());
    }

    [Fact]
    public void AnEntryAppendedWithoutAClaimHasNone()
    {
        var rig = Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "pull", ["created"] = "2026-01-01T11:00:00.000Z", ["text"] = "t" },
            null);

        Assert.Null(rig.Claim("old"));
        Assert.False(rig.Fx.Mailbox.Claim(rig.BoxB, "old", ClaimActions.Take, new JsonObject { ["session_id"] = "s-a" }).Won);
        Assert.Equal("none", rig.Fx.Mailbox.Claim(rig.BoxB, "old", ClaimActions.Advance).State);
    }

    [Fact]
    public void TheSenderSessionTakesThePendingClaimAndOnlyTheSameToolCallMayTakeItAgain()
    {
        var rig = Create();
        rig.Send("e1");
        var holder = new JsonObject { ["session_id"] = "s-a", ["pid"] = 4242, ["socket"] = "uds:/tmp/cc-socks/4242.sock" };
        JsonObject Take(string? call) =>
            call is null
                ? new JsonObject { ["session_id"] = "s-a", ["holder"] = holder }
                : new JsonObject { ["session_id"] = "s-a", ["holder"] = holder, ["tool_use_id"] = call };

        var first = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, Take("toolu_1"));
        var sameCall = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, Take("toolu_1"));
        var otherCall = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, Take("toolu_2"));
        var noCall = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, Take(null));

        Assert.True(first.Won);
        Assert.Equal("relay-claimed", first.State);
        Assert.True(sameCall.Won);
        Assert.False(otherCall.Won);
        Assert.Equal("held-by-self", otherCall.Reason);
        Assert.False(noCall.Won);
        var holderRecord = rig.Claim("e1")!.Get("holder")!.Value;
        Assert.Equal("s-a", holderRecord.Member("session_id")!.Value.AsString());
        Assert.Equal("toolu_1", holderRecord.Member("tool_use_id")!.Value.AsString());
    }

    [Fact]
    public void ASessionThatIsNotTheEntrysSenderCannotTakeTheClaim()
    {
        var rig = Create();
        rig.Send("e1");

        var result = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-other" });

        Assert.False(result.Won);
        Assert.Equal("pending", result.State);
        Assert.Equal("pending", rig.Claim("e1")!.Get("state")!.Value.AsString());
    }

    [Fact]
    public void ACarrierPromptEntryCannotBeTakenEvenByItsSender()
    {
        var rig = Create(receiverHarness: "codex");
        rig.Send("e1");

        var result = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-a" });

        Assert.False(result.Won);
        Assert.Equal("carrier-prompt", result.Reason);
    }

    [Fact]
    public void AClaimTakenByAnotherSessionIsNotWonByASecond()
    {
        var rig = Create();
        rig.Send("e1");
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-a" });

        var second = rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-other" });

        Assert.False(second.Won);
        Assert.Equal("relay-claimed", second.State);
    }

    [Fact]
    public void EveryTransitionIsOneAttemptRowOfTheEntry()
    {
        var rig = Create();
        rig.Send("e1");
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Take, new JsonObject { ["session_id"] = "s-a" });
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Confirm, new JsonObject { ["session_id"] = "s-a" });

        var rows = rig.Fx.Mailbox.ReadAttempts(rig.BoxB)
            .Where(r => r.Get("claim") is { IsBool: true } c && c.AsBool() && r.Get("id")!.Value.AsString() == "e1")
            .Select(r => r.Get("state")!.Value.AsString())
            .ToList();

        Assert.Equal(["claim-relay-claimed", "claim-relay-sent"], rows);
        Assert.Empty(rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1"));
    }

    [Fact]
    public void OfAFallbackAndARelayClaimOnOnePendingEntryExactlyOneWins()
    {
        var sandbox = UnitSandbox.Create();
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var fx = MeshFixture.Create(vars, Instant, sandbox.Root, singleCarrier: true);
        var io = PlatformModule.CreateIo(null);
        var box = sandbox.Root + "/ah/mailbox/tm~aaaaaaaaaa/b";
        var context = new RequestContext(string.Empty, string.Empty, string.Empty, string.Empty);
        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, new FakeClock(Instant), [fx.Mailbox.StateDomain()], 4242);
        StateResult Run(string op, JsonObject args, string operationId) =>
            engine("mailbox", op, JsonText.ToUtf8(args, 0), context, operationId, TimeSpan.FromSeconds(20));

        for (var i = 0; i < 24; i++)
        {
            var id = $"c{i}";
            var created = "2026-01-01T11:00:00.000Z";
            var claim = new JsonObject
            {
                ["v"] = 1, ["id"] = id, ["state"] = "pending", ["carrier"] = "relay", ["from"] = "a@tm", ["to"] = "b@tm",
                ["sender_session"] = "s-a", ["receiver_session"] = "s-b", ["l0_ms"] = 180000, ["l1_ms"] = 60000, ["created"] = created, ["at"] = created,
            };
            var appended = Run(
                "append",
                new JsonObject
                {
                    ["dir"] = box,
                    ["entry"] = new JsonObject { ["v"] = 1, ["id"] = id, ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "pull", ["created"] = created, ["text"] = "t" },
                    ["claim"] = claim,
                },
                $"a-{i}");
            Assert.True(appended.Ok, appended.Error);
        }

        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool TakeWon, string Final)>();
        Parallel.For(
            0,
            48,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            n =>
            {
                var id = $"c{n / 2}";
                var args = n % 2 == 0
                    ? new JsonObject { ["dir"] = box, ["id"] = id, ["action"] = "take", ["session_id"] = "s-a" }
                    : new JsonObject { ["dir"] = box, ["id"] = id, ["action"] = "advance" };
                var result = Run("claim", args, $"c-{n}");
                Assert.True(result.Ok, result.Error);
                if (n % 2 == 0)
                {
                    var won = JsonText.Parse(result.ResultJson.Span).Member("won") is { IsBool: true } w && w.AsBool();
                    outcomes.AddOrUpdate(id, (won, string.Empty), (_, old) => (won, old.Final));
                }
            });

        for (var i = 0; i < 24; i++)
        {
            var id = $"c{i}";
            var record = fx.Mailbox.ReadClaim(box, id)!;
            var state = record.Get("state")!.Value.AsString();
            var rows = fx.Mailbox.ReadAttempts(box)
                .Where(r => r.Get("claim") is { IsBool: true } && r.Get("id")!.Value.AsString() == id)
                .Select(r => r.Get("state")!.Value.AsString())
                .ToList();
            Assert.True(state is "relay-claimed" or "fallback-claimed", state);
            if (outcomes[id].TakeWon)
            {
                Assert.Equal("relay-claimed", state);
                Assert.DoesNotContain("claim-fallback-claimed", rows);
            }
            else
            {
                Assert.Equal("fallback-claimed", state);
                Assert.DoesNotContain("claim-relay-claimed", rows);
            }
        }
    }
}
