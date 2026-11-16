using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The briefs a session's boxes still owe a report on, whoever sent them and whether or not anything acked them.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class OwedBriefsTests
{
    private const string Session = "s-a";

    private sealed record Rig(MeshFixture Fx, string Hierarchy, string Box)
    {
        public string Request => Hierarchy + "/msgs/20260101-000000-aa11--a--work--request.md";

        public IReadOnlyList<OpenBrief> Owed() => Fx.Hooks.OwedBriefs(Session);

        public void Entry(string json) => File.AppendAllText(Box + "/entries.jsonl", json + "\n");
    }

    private static Rig Create()
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        Directory.CreateDirectory(hierarchy + "/msgs");
        File.WriteAllText(
            hierarchy + "/teams/tm.json",
            "{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n");
        var home = sandbox.Root + "/ah";
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = home };
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root);
        var box = sandbox.Root + "/box-a";
        Directory.CreateDirectory(box);
        var rig = new Rig(fx, hierarchy, box);
        File.WriteAllText(
            rig.Request,
            "---\nid: 20260101-000000-aa11\ntype: request\nto: a\nfrom: b\nslug: work\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-01-01T00:00:00+00:00\n---\n\nbody\n");
        Directory.CreateDirectory(home + "/registrations");
        File.WriteAllText(
            home + "/registrations/" + Session + ".json",
            "{\"session_id\":\"" + Session + "\",\"address\":\"a@tm\",\"addresses\":[\"a@tm\"],\"boxes\":{\"a@tm\":\"" + box + "\"}}\n");
        return rig;
    }

    private static string BriefJson(Rig rig, string id = "e1", string kind = "brief", bool withRef = true) =>
        "{\"id\":\"" + id + "\",\"kind\":\"" + kind + "\"" + (withRef ? ",\"ref\":\"" + rig.Request + "\"" : string.Empty) + ",\"from\":\"b@tm\",\"to\":\"a@tm\"}";

    [Fact]
    public void ABriefWhoseReportIsNotSentIsOwedWithoutAnyAck()
    {
        var rig = Create();
        rig.Entry(BriefJson(rig));

        var owed = Assert.Single(rig.Owed());

        Assert.Equal("e1", owed.Id);
        Assert.Equal(rig.Request, owed.Ref);
    }

    [Fact]
    public void ABriefIsNoLongerOwedOnceItsReportIsSent()
    {
        var rig = Create();
        rig.Entry(BriefJson(rig));
        _ = rig.Fx.Hooks.ReportStateOf(Session, new OpenBrief("e1", rig.Request, "b@tm"), out var path);
        File.WriteAllText(path!, "DONE\n");
        var sent = rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["ref"] = path!, ["id"] = "r-a", ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy });
        Assert.True(sent.Failure is null, "the report send failed");

        Assert.Empty(rig.Owed());
    }

    [Fact]
    public void AnEntryThatIsNotABriefOrHasNoRefOwesNothing()
    {
        var rig = Create();
        rig.Entry(BriefJson(rig, "n1", kind: "note"));
        rig.Entry(BriefJson(rig, "n2", kind: "report"));
        rig.Entry(BriefJson(rig, "n3", withRef: false));

        Assert.Empty(rig.Owed());
    }

    [Fact]
    public void ASessionWithNoRegistrationOwesNothing()
    {
        var rig = Create();
        rig.Entry(BriefJson(rig));

        Assert.Empty(rig.Fx.Hooks.OwedBriefs("no-such-session"));
    }
}
