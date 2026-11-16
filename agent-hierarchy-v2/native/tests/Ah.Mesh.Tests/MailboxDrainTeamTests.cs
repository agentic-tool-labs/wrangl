using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The sweep a quit, a stop and a resume make over a team's boxes: unacked mail gets a drained row once, and the rows a hold wrote can be listed again.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailboxDrainTeamTests
{
    private const string Since = "2026-01-01T11:00:00.000Z";

    private sealed record Rig(MeshFixture Fx, string TeamFile, string Box)
    {
        public void Mail(string id, string text = "hello")
        {
            Directory.CreateDirectory(Box);
            var entry = new JsonObject { ["id"] = id, ["kind"] = "note", ["from"] = "user", ["to"] = "b", ["text"] = text, ["created"] = "2026-01-01T10:00:00.000Z" };
            File.AppendAllText(Box + "/entries.jsonl", JsonText.Stringify(entry, 0) + "\n");
        }

        public string LastState(string id)
        {
            var rows = Fx.Mailbox.LadderRowsOf(Box, id);
            return rows.Count > 0 && rows[^1].Get("state") is { IsText: true } s ? s.AsString() : string.Empty;
        }
    }

    private static EffectiveHold Hold(string team, string since = Since, string kind = "quit") =>
        new(team, null, "/h/holds/" + team + "/hold.json", kind, "winding-down", since, new JsonObject(), null);

    private static Rig Create(string teamFileName)
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        var teamFile = hierarchy + "/" + teamFileName;
        File.WriteAllText(teamFile, "{\"members\":[{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n");
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root);
        return new Rig(fx, teamFile, fx.Mailbox.BoxDirFor(teamFile, "b"));
    }

    [Fact]
    public void ANamedTeamsUnackedMailGetsOneDrainedRowAndASecondSweepDrainsNothing()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("m1");

        var first = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));
        var second = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));

        Assert.Equal(1, (int)first.Get("entries")!.Value.AsNumber());
        Assert.Equal("drained", rig.LastState("m1"));
        Assert.Equal(0, (int)second.Get("entries")!.Value.AsNumber());
        Assert.Single(rig.Fx.Mailbox.LadderRowsOf(rig.Box, "m1"));
    }

    [Fact]
    public void WhatAHoldDrainedIsQueuedOnceUnderANewIdAndTheOriginalStaysClosed()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("m1");
        _ = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));

        var first = rig.Fx.Mailbox.RedeliverDrained(rig.TeamFile, "tm", Hold("tm"));
        var second = rig.Fx.Mailbox.RedeliverDrained(rig.TeamFile, "tm", Hold("tm"));

        Assert.Equal(1, (int)first.Get("entries")!.Value.AsNumber());
        Assert.Equal(0, (int)second.Get("entries")!.Value.AsNumber());
        var entries = rig.Fx.Mailbox.ReadEntries(rig.Box).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("m1", entries[1].Member("redelivers")!.Value.AsString());
        Assert.Equal("hello", entries[1].Member("text")!.Value.AsString());
        Assert.Equal("drained", rig.LastState("m1"));
        Assert.Equal(string.Empty, rig.LastState("m1-held"));
    }

    [Fact]
    public void NegativeAnotherHoldsRowsEventsAndAQuitsOwnBriefAreNotQueued()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("older");
        _ = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm", since: "2025-12-01T11:00:00.000Z"));
        var box = rig.Box + "/entries.jsonl";
        File.AppendAllText(box, "{\"id\":\"exempt\",\"kind\":\"note\",\"from\":\"user\",\"to\":\"b\",\"text\":\"save your work\",\"hold_exempt\":true,\"created\":\"2026-01-01T10:00:00.000Z\",\"route\":\"pull\"}\n");
        File.AppendAllText(box, "{\"id\":\"ev\",\"kind\":\"event\",\"from\":\"ahd\",\"to\":\"b\",\"event\":\"x\",\"created\":\"2026-01-01T10:00:00.000Z\",\"route\":\"pull\"}\n");
        _ = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));

        var report = rig.Fx.Mailbox.RedeliverDrained(rig.TeamFile, "tm", Hold("tm"));

        Assert.Equal(0, (int)report.Get("entries")!.Value.AsNumber());
        Assert.Equal(3, rig.Fx.Mailbox.ReadEntries(rig.Box).Entries.Count);
    }

    [Fact]
    public void TheDefaultTeamsUnackedMailIsDrainedLikeANamedTeams()
    {
        var rig = Create("team.json");
        rig.Mail("m1");

        var report = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "_default", Hold("_default"));

        Assert.Equal(1, (int)report.Get("entries")!.Value.AsNumber());
        Assert.Equal("drained", rig.LastState("m1"));
        Assert.Equal("b@_default", report.Get("rows")!.Value.AsArray()[0].AsObject().Get("box")!.Value.AsString());
    }

    [Fact]
    public void AnAckedEntryIsNotDrained()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("m1");
        _ = rig.Fx.Mailbox.AckIds(rig.Box, ["m1"], "test");

        var report = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));

        Assert.Equal(0, (int)report.Get("entries")!.Value.AsNumber());
        Assert.NotEqual("drained", rig.LastState("m1"));
    }

    [Fact]
    public void TheRowsAHoldDrainedCanBeListedAgainByThatHoldAndNotByAnother()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("m1");
        rig.Mail("m2");
        _ = rig.Fx.Mailbox.DrainTeam(rig.TeamFile, "tm", Hold("tm"));

        var same = rig.Fx.Mailbox.DrainedBy(rig.TeamFile, "tm", Hold("tm"));
        var later = rig.Fx.Mailbox.DrainedBy(rig.TeamFile, "tm", Hold("tm", since: "2026-01-02T11:00:00.000Z"));

        Assert.Equal(["m1", "m2"], same.Get("rows")!.Value.AsArray().Select(r => r.AsObject().Get("id")!.Value.AsString()).ToArray());
        Assert.Empty(later.Get("rows")!.Value.AsArray());
    }

    [Fact]
    public void AnEntryThatWasNotDrainedIsNotListedAsDrainedByTheHold()
    {
        var rig = Create("teams/tm.json");
        rig.Mail("m1");

        var listed = rig.Fx.Mailbox.DrainedBy(rig.TeamFile, "tm", Hold("tm"));

        Assert.Empty(listed.Get("rows")!.Value.AsArray());
    }
}
