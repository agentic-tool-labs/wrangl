using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// A team record written before Orchestrator start times were recorded takes the start of its session's live registration, but
/// only when that registration's pid is the recorded pid; otherwise nothing is written.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class OrchestratorStartBackfillTests
{
    private const string Session = "orch-s1";

    private readonly MeshFixture _fx;
    private readonly string _teamFile;

    public OrchestratorStartBackfillTests()
    {
        var sandbox = UnitSandbox.Create();
        var project = sandbox.Root + "/proj";
        Directory.CreateDirectory(project);
        var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah", ["AH_TEST"] = "1" };
        _fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture), project);
        var hierarchy = _fx.Callers.RequestContext(project).HierarchyDirectory;
        _teamFile = _fx.Roster.TeamPath(hierarchy, "tm");
        Directory.CreateDirectory(Path.GetDirectoryName(_teamFile)!);
        var legacy = new JsonObject { ["session_id"] = Session, ["pid"] = Environment.ProcessId };
        File.WriteAllText(_teamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = legacy, ["members"] = new JsonArray() }, 0));
    }

    private string Start => _fx.OwnStart!;

    private void Register(long pid) =>
        Assert.True(_fx.Mailbox.WriteRegistration(new RegistrationWrite(
            Session, [new BoxRef("orchestrator@tm", _fx.Mailbox.BoxDirFor(_teamFile, "orchestrator"))], _teamFile, "claude", pid, PidStart: _fx.Table.StartTime(pid), Bound: true)));

    private JsonObject Orchestrator() => JsonText.Parse(File.ReadAllText(_teamFile)).Member("orchestrator")!.Value.AsObject();

    [Fact]
    public void ALegacyRecordWhoseSessionIsRegisteredAtTheSamePidTakesItsStart()
    {
        Register(Environment.ProcessId);

        Assert.Equal(1, _fx.Callers.PinOrchestratorStarts());

        Assert.Equal(Start, Orchestrator()["pid_start"].AsString());
        Assert.True(_fx.Roster.IsTeamOwnedBy(JsonText.Parse(File.ReadAllText(_teamFile)), new Ah.Abstractions.TeamInvoker(Environment.ProcessId, null)));
    }

    [Fact]
    public void ALegacyRecordWhoseRegistrationHasAnotherPidIsNotBackfilled()
    {
        Register(Environment.ProcessId);
        var other = new JsonObject { ["session_id"] = Session, ["pid"] = 2147483000 };
        File.WriteAllText(_teamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = other, ["members"] = new JsonArray() }, 0));
        var before = File.ReadAllText(_teamFile);

        Assert.Equal(0, _fx.Callers.PinOrchestratorStarts());

        Assert.Equal(before, File.ReadAllText(_teamFile));
    }

    [Fact]
    public void ALegacyRecordWithNoRegistrationIsNotBackfilledAndOwnsNothing()
    {
        var before = File.ReadAllText(_teamFile);

        Assert.Equal(0, _fx.Callers.PinOrchestratorStarts());

        Assert.Equal(before, File.ReadAllText(_teamFile));
        Assert.False(_fx.Roster.IsTeamOwnedBy(JsonText.Parse(before), new Ah.Abstractions.TeamInvoker(Environment.ProcessId, null)));
    }

    [Fact]
    public void ARecordThatAlreadyHasAStartIsLeftAsItIs()
    {
        Register(Environment.ProcessId);
        var pinned = new JsonObject { ["session_id"] = Session, ["pid"] = Environment.ProcessId, ["pid_start"] = "Mon Jan  1 00:00:00 2001" };
        File.WriteAllText(_teamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = pinned, ["members"] = new JsonArray() }, 0));
        var before = File.ReadAllText(_teamFile);

        Assert.Equal(0, _fx.Callers.PinOrchestratorStarts());

        Assert.Equal(before, File.ReadAllText(_teamFile));
    }
}
