using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// A team whose Orchestrator record went stale across <c>/clear</c> (same process, new session) or <c>--resume</c> (same session,
/// new process) is repaired for a (session, process) pair the caller proved; a team of anyone else is never touched.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class RebindOwnedTeamsTests
{
    private const string Session = "sess-new";

    private sealed class Rig : IDisposable
    {
        private readonly List<Process> _children = [];

        internal Rig(bool testMode = false, string? projectRoot = null)
        {
            var sandbox = UnitSandbox.Create();
            Project = (projectRoot ?? sandbox.Root) + "/proj";
            Directory.CreateDirectory(Project);
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = sandbox.Root + "/ah" };
            if (testMode)
            {
                vars["AH_TEST"] = "1";
            }

            Fx = MeshFixture.Create(vars, DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", CultureInfo.InvariantCulture), Project);
            HierarchyDir = Fx.Callers.RequestContext(Project).HierarchyDirectory;
            Directory.CreateDirectory(HierarchyDir);
        }

        internal MeshFixture Fx { get; }

        internal string Project { get; }

        internal string HierarchyDir { get; }

        internal Process Child()
        {
            var child = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
            _children.Add(child);
            return child;
        }

        internal int DeadPid()
        {
            var child = Child();
            child.Kill();
            child.WaitForExit();
            return child.Id;
        }

        internal string Team(string name, double? pid, string? session, string? created = null, string? pidStart = null, string? dir = null)
        {
            var orchestrator = new JsonObject { ["session_id"] = session is null ? JsonValue.Null : session };
            if (pid is not null)
            {
                orchestrator["pid"] = pid.Value;
            }

            if (pidStart is not null)
            {
                orchestrator["pid_start"] = pidStart;
            }

            var team = new JsonObject
            {
                ["created"] = created ?? DateTimeOffset.Now.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["orchestrator"] = orchestrator,
                ["members"] = new JsonArray(),
            };
            var file = Fx.Roster.TeamPath(dir ?? HierarchyDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonText.Stringify(team, 0));
            return file;
        }

        internal static JsonObject Orchestrator(string file) =>
            JsonText.Parse(File.ReadAllText(file)).AsObject().Get("orchestrator")!.Value.AsObject();

        internal RebindResult Rebind(int pid, RebindModes modes = RebindModes.Both) =>
            Fx.Callers.RebindOwnedTeams(new RebindRequest(Project, Session, pid, modes));

        internal static string PsStart(int pid)
        {
            var ps = Process.Start(new ProcessStartInfo("/bin/ps", $"-o lstart= -p {pid}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                Environment = { ["LC_ALL"] = "C", ["TZ"] = "UTC" },
            })!;
            var text = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return text;
        }

        public void Dispose()
        {
            foreach (var child in _children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                        child.WaitForExit();
                    }
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }

                child.Dispose();
            }
        }
    }

    [Fact]
    public void AfterClearTheTeamOfThisProcessTakesTheNewSession()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var file = rig.Team("tm", p.Id, "sess-old");

        var result = rig.Rebind(p.Id);

        Assert.True(result.Notes.Count == 0, string.Join("|", result.Notes));
        Assert.Equal(Session, Rig.Orchestrator(file).Get("session_id")!.Value.AsString());
        Assert.Equal("session_id", Assert.Single(result.Changed).Field);
    }

    [Fact]
    public void ATeamCreatedBeforeThisProcessStartedIsNotRebound()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var file = rig.Team("tm", p.Id, "sess-old", created: "2001-01-01T00:00:00+00:00");

        var result = rig.Rebind(p.Id);

        Assert.Equal("sess-old", Rig.Orchestrator(file).Get("session_id")!.Value.AsString());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void ATeamOfAnotherLiveProcessIsNotRebound()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var other = rig.Child();
        var file = rig.Team("tm", other.Id, "sess-old");

        var result = rig.Rebind(p.Id);

        Assert.Equal("sess-old", Rig.Orchestrator(file).Get("session_id")!.Value.AsString());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void AResumedSessionReclaimsItsTeamAndRecordsTheStartTime()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var file = rig.Team("tm", rig.DeadPid(), Session);

        var result = rig.Rebind(p.Id);

        var orchestrator = Rig.Orchestrator(file);
        Assert.Equal(p.Id, (int)orchestrator.Get("pid")!.Value.AsNumber());
        Assert.Equal(Rig.PsStart(p.Id), orchestrator.Get("pid_start")!.Value.AsString());
        Assert.Equal("pid", Assert.Single(result.Changed).Field);
    }

    [Fact]
    public void AResumedLaneChildChangesOnlyItsOwnersPidAndStartAndNoOtherFile()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var child = rig.Team("tm-ws1", rig.DeadPid(), Session, pidStart: "Mon Jan  1 00:00:00 2001");
        var manager = rig.Team("tm", rig.Child().Id, "sess-mo");
        File.WriteAllText(child, File.ReadAllText(child).Replace("\"members\"", "\"parent\":{\"team\":\"tm\",\"stream\":\"ws1\"},\"members\"", StringComparison.Ordinal));
        var childBefore = File.ReadAllText(child);
        var managerBefore = File.ReadAllText(manager);

        var result = rig.Rebind(p.Id);

        Assert.Equal("pid", Assert.Single(result.Changed).Field);
        Assert.Equal(managerBefore, File.ReadAllText(manager));
        static string Without(string json)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var owner = node["orchestrator"]!.AsObject();
            owner.Remove("pid");
            owner.Remove("pid_start");
            return node.ToJsonString();
        }

        Assert.Equal(Without(childBefore), Without(File.ReadAllText(child)));
        Assert.Equal(Rig.PsStart(p.Id), Rig.Orchestrator(child).Get("pid_start")!.Value.AsString());
    }

    [Fact]
    public void ARestoredOwnerWithNoRecordedSessionIsNotRebound()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var dead = rig.DeadPid();
        var file = rig.Team("tm", dead, null);

        var result = rig.Rebind(p.Id);

        Assert.Equal(dead, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void ASessionOnlyRebindNeverRestampsThePid()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var dead = rig.DeadPid();
        var file = rig.Team("tm", dead, Session);

        var result = rig.Rebind(p.Id, RebindModes.Session);

        Assert.Equal(dead, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void ALiveRecordedOwnerIsNeverReplaced()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var q = rig.Child();
        var file = rig.Team("tm", q.Id, Session, pidStart: Rig.PsStart(q.Id));

        var result = rig.Rebind(p.Id);

        Assert.Equal(q.Id, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void AReusedLivePidWithADifferentStartTimeCountsAsDeadForResume()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var q = rig.Child();
        var file = rig.Team("tm", q.Id, Session, pidStart: "Mon Jan  1 00:00:00 2001");

        var result = rig.Rebind(p.Id);

        Assert.Equal(p.Id, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Equal("pid", Assert.Single(result.Changed).Field);
    }

    [Fact]
    public void ANewSessionDoesNotTouchAnOrphan()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var dead = rig.DeadPid();
        var file = rig.Team("tm", dead, "sess-someone-else");

        var result = rig.Rebind(p.Id);

        var orchestrator = Rig.Orchestrator(file);
        Assert.Equal(dead, (int)orchestrator.Get("pid")!.Value.AsNumber());
        Assert.Equal("sess-someone-else", orchestrator.Get("session_id")!.Value.AsString());
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void ACrossRepoTeamIsReclaimedFromTheSessionsOwnRegistration()
    {
        using var rig = new Rig();
        var p = rig.Child();
        var elsewhere = UnitSandbox.Create().Root + "/other/.claude/ah2";
        var file = rig.Team("far", rig.DeadPid(), Session, dir: elsewhere);
        _ = rig.Fx.Mailbox.RegisterGuarded(
            new RegistrationWrite(
                Session,
                [new BoxRef("orchestrator@far", rig.Fx.Mailbox.BoxDirFor(file, "orchestrator"))],
                TeamFile: file,
                TeamFiles: new Dictionary<string, string> { ["orchestrator@far"] = file },
                Pid: p.Id),
            null);

        var result = rig.Rebind(p.Id);

        Assert.Equal(p.Id, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Equal("far", Assert.Single(result.Changed).Team);
    }

    [Fact]
    public void ATestModeRebindNeverTouchesATeamOutsideTheSandbox()
    {
        // /var/tmp, not the temp path: a harness points TMPDIR under an ahprobe sandbox, where this team would be inside it
        var outside = Directory.CreateDirectory(Path.Combine("/var/tmp", "rebind-outside-" + Guid.NewGuid().ToString("N"))).FullName;
        Assert.DoesNotContain("ahprobe.", outside, StringComparison.Ordinal);
        try
        {
            using var rig = new Rig(testMode: true);
            var p = rig.Child();
            var dead = rig.DeadPid();
            var file = rig.Team("far", dead, Session, dir: outside + "/.claude/ah2");
            _ = rig.Fx.Mailbox.RegisterGuarded(
                new RegistrationWrite(
                    Session,
                    [new BoxRef("orchestrator@far", rig.Fx.Mailbox.BoxDirFor(file, "orchestrator"))],
                    TeamFile: file,
                    TeamFiles: new Dictionary<string, string> { ["orchestrator@far"] = file },
                    Pid: p.Id),
                null);

            var result = rig.Rebind(p.Id);

            Assert.Equal(dead, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
            Assert.Empty(result.Changed);
            Assert.Contains(result.Notes, n => n.StartsWith("rebind skipped: test mode never writes", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void ATestModeRebindStillRepairsATeamInsideTheSandbox()
    {
        using var rig = new Rig(testMode: true);
        var p = rig.Child();
        var file = rig.Team("tm", rig.DeadPid(), Session);

        var result = rig.Rebind(p.Id);

        Assert.Equal(p.Id, (int)Rig.Orchestrator(file).Get("pid")!.Value.AsNumber());
        Assert.Empty(result.Notes);
    }
}
