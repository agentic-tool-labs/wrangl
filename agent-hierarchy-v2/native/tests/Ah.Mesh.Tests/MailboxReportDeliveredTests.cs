using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>Whether a report is already in the box of the session reading it: the check that stops "never sent" being said of a delivered report.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailboxReportDeliveredTests
{
    private const string Reader = "s-b";

    private sealed record Rig(MeshFixture Fx, string Hierarchy)
    {
        public string Response => Hierarchy + "/msgs/20260101-000000-aa11--b--work--response.md";

        public string Other => Hierarchy + "/msgs/20260101-000000-bb22--b--other--response.md";

        public void Send(string path) =>
            Assert.True(Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["ref"] = path, ["id"] = "r-" + Path.GetFileName(path)[18..22], ["cwd"] = Hierarchy, ["hierarchy_dir"] = Hierarchy }).Failure is null);
    }

    private static Rig Create()
    {
        var sandbox = UnitSandbox.Create();
        var hierarchy = sandbox.Root + "/h";
        Directory.CreateDirectory(hierarchy + "/teams");
        Directory.CreateDirectory(hierarchy + "/msgs");
        var teamFile = hierarchy + "/teams/tm.json";
        File.WriteAllText(
            teamFile,
            "{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"kind\":\"claude\"},{\"name\":\"b\",\"role\":\"reviewer\",\"kind\":\"claude\"}],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n");
        var home = sandbox.Root + "/ah";
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = home };
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-01-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), sandbox.Root);
        var rig = new Rig(fx, hierarchy);
        File.WriteAllText(rig.Response, "---\nid: 20260101-000000-aa11\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.WriteAllText(rig.Other, "---\nid: 20260101-000000-bb22\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        var box = fx.Mailbox.BoxDirFor(teamFile, "b");
        Directory.CreateDirectory(home + "/registrations");
        File.WriteAllText(
            home + "/registrations/" + Reader + ".json",
            "{\"session_id\":\"" + Reader + "\",\"address\":\"b@tm\",\"addresses\":[\"b@tm\"],\"boxes\":{\"b@tm\":\"" + box + "\"}}\n");
        return rig;
    }

    [Fact]
    public void AReportNobodySentIsNotDelivered()
    {
        var rig = Create();

        Assert.False(rig.Fx.Hooks.ReportDelivered(Reader, rig.Response));
    }

    [Fact]
    public void AReportInTheReadersBoxIsDeliveredWhetherOrNotItWasRead()
    {
        var rig = Create();
        rig.Send(rig.Response);

        Assert.True(rig.Fx.Hooks.ReportDelivered(Reader, rig.Response));
    }

    [Fact]
    public void AnotherResponseFilesReportDoesNotCount()
    {
        var rig = Create();
        rig.Send(rig.Other);

        Assert.False(rig.Fx.Hooks.ReportDelivered(Reader, rig.Response));
    }

    [Fact]
    public void ASessionWithNoRegistrationHasNothingDelivered()
    {
        var rig = Create();
        rig.Send(rig.Response);

        Assert.False(rig.Fx.Hooks.ReportDelivered("s-unknown", rig.Response));
    }
}
