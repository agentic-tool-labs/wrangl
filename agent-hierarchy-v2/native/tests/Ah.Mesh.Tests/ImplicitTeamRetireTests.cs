using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>ah2-470 D2, ah2-476: resolving a caller clears the empty implicit team that yielded to another owned team, unless it has a checklist row not done.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ImplicitTeamRetireTests
{
    private const string Session = "orch-s";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AYieldedImplicitTeamIsClearedUnlessARowIsNotDone(bool pendingRow)
    {
        var sandbox = UnitSandbox.Create();
        var project = sandbox.Root + "/proj";
        Directory.CreateDirectory(project);
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah", ["AH_TEST"] = "1" };
        var fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), project);
        var dir = fx.Callers.RequestContext(project).HierarchyDirectory;
        var named = fx.Roster.TeamPath(dir, "tm");
        var implicitTeam = fx.Roster.TeamPath(dir, "imp");
        Directory.CreateDirectory(Path.GetDirectoryName(named)!);
        Directory.CreateDirectory(Path.GetDirectoryName(implicitTeam)!);
        JsonObject Orchestrator() => new() { ["session_id"] = Session, ["pid"] = Environment.ProcessId, ["pid_start"] = fx.OwnStart! };
        File.WriteAllText(named, JsonText.Stringify(new JsonObject { ["orchestrator"] = Orchestrator(), ["members"] = new JsonArray() }, 0));
        File.WriteAllText(implicitTeam, JsonText.Stringify(new JsonObject { ["implicit"] = true, ["orchestrator"] = Orchestrator(), ["members"] = new JsonArray() }, 0));
        if (pendingRow)
        {
            Assert.Null(fx.Hierarchy.Progress.RecordChecklist(dir, "imp", "_run", "orchestrator@imp", [new PlanInput("a", "add", "Do a")]).Refused);
        }

        var boxes = fx.Callers.ResolveCaller(new CallerRequest(project, Session, Environment.ProcessId)).Select(b => b.Address).ToList();

        Assert.Contains("orchestrator@tm", boxes);
        Assert.DoesNotContain("orchestrator@imp", boxes);
        Assert.Equal(pendingRow, File.Exists(implicitTeam));
        Assert.True(File.Exists(named));
    }
}
