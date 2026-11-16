using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>A relay line the prompt neutralised still resolves the obligation of the session that holds it, and nobody else's.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailboxHooksRelayTests
{
    private const string Holder = "s-holder";

    private sealed record Rig(MeshFixture Fx, RequestContext Context, string BoxA, string BoxB);

    private static Rig Create(bool twoImplementors = false)
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        var second = twoImplementors ? "{\"name\":\"c\",\"role\":\"implementor\",\"kind\":\"claude\"}," : string.Empty;
        File.WriteAllText(
            hierarchy + "/teams/tm.json",
            "{\"members\":[" + second + "{\"name\":\"a\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n");
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root);
        var team = hierarchy + "/teams/tm.json";
        var boxA = fx.Mailbox.BoxDirFor(team, "a");
        var boxB = fx.Mailbox.BoxDirFor(team, "b");
        Append(fx, boxA, "e1");
        Append(fx, boxA, "e2");
        Append(fx, boxB, "e3");
        var relay = new JsonObject
        {
            ["entry"] = "e1",
            ["address"] = "a@tm",
            ["dir"] = boxA,
            ["to"] = "a@tm",
            ["line"] = "[hierarchy-msg /m/x--request.md] [ah-entry e1 to a@tm]",
        };
        Assert.True(fx.Hierarchy.Peers.AppendRelayRecord(Holder, relay));
        return new Rig(fx, new RequestContext(hierarchy, hierarchy, string.Empty, string.Empty), boxA, boxB);
    }

    private static void Append(MeshFixture fx, string box, string id) =>
        fx.Mailbox.AppendEntry(
            box,
            new JsonObject { ["v"] = 1, ["id"] = id, ["from"] = "b@tm", ["to"] = "a@tm", ["kind"] = "note", ["route"] = "pull", ["created"] = "2026-01-01T11:59:59.000Z", ["text"] = "t-" + id },
            null);

    [Fact]
    public void ANeutralisedRelayFromTheObligationHolderAcksTheEntry()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "[hierarchy-msg /m/x--request.md] (ah-entry e1 to a@tm]", rig.Context);

        Assert.Equal(["e1"], acked);
        Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
        Assert.Equal("sendmessage", rig.Fx.Mailbox.ReadAck(rig.BoxA, "e1")!.Value.AsObject().Get("by")!.Value.AsString());
    }

    [Fact]
    public void ANeutralisedMarkerFromSomeoneWithoutTheObligationAcksNothing()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage("s-other", "a@tm", "(ah-entry e1 to a@tm]", rig.Context);

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void ANeutralisedMarkerToTheWrongRecipientAcksNothing()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "b@tm", "(ah-entry e1 to a@tm]", rig.Context);

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void ANeutralisedMarkerNamingAnotherEntryAcksNothing()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "(ah-entry e2 to a@tm]", rig.Context);

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e2"));
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void ANeutralisedMarkerNamingAnotherAddressAcksNothing()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "(ah-entry e1 to b@tm]", rig.Context);

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void AStrictMarkerStillAcksWithoutAnObligation()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage("s-other", "a@tm", "[ah-entry e2 to a@tm]", rig.Context);

        Assert.Equal(["e2"], acked);
    }

    [Fact]
    public void ARoleFormRelayResolvesAMemberFormObligation()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "[hierarchy-msg /m/x--request.md] [ah-entry e1 to implementor@tm]", rig.Context);

        Assert.Equal(["e1"], acked);
        Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void ANeutralisedRoleFormRelayResolvesAMemberFormObligation()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "(ah-entry e1 to implementor@tm]", rig.Context);

        Assert.Equal(["e1"], acked);
    }

    [Fact]
    public void AnAmbiguousRoleFormMatchesNothing()
    {
        var rig = Create(twoImplementors: true);

        var strict = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "[ah-entry e1 to implementor@tm]", rig.Context);
        var neutralised = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "(ah-entry e1 to implementor@tm]", rig.Context);

        Assert.Empty(strict);
        Assert.Empty(neutralised);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }

    [Fact]
    public void ARoleFormOfAnotherMemberMatchesNothing()
    {
        var rig = Create();

        var acked = rig.Fx.Hooks.AckFromSendMessage(Holder, "a@tm", "[ah-entry e1 to reviewer@tm]", rig.Context);

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxA, "e1"));
    }
}
