using System.Text;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>The AH MEMORY block at session start: present for a session that owns a team and for each source that recalls, absent for a member.</summary>
public sealed class MemoryRecallTests
{
    private const string LedgerRow =
        """{"v":1,"at":"2026-10-23T10:00:00.000Z","kind":"decision","team":"tm","stream":null,"sha":null,"base":null,"ref":null,"verdict":null,"slug":"foo","text":"use A","source":{"type":"report","path":null,"verb":null,"tool_use_id":null}}""";

    private static string BigRow(int i) => LedgerRow.Replace("use A", $"decision {i} " + new string('x', 180), StringComparison.Ordinal);

    private static string Context(string source, bool owner, bool withLedger = true, string[]? teams = null, bool big = false)
    {
        teams ??= ["tm"];
        var rig = new HooksHostTests.Rig();
        var dir = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/agent-hierarchy.json", """{"version":1,"alwaysOrchestrator":true}""");
        if (withLedger)
        {
            var cwd = rig.Sandbox.Root + "/proj";
            var pool = Ah.Hierarchy.HierarchyModule.ProgressPoolOf(rig.Services.Config.MainHierarchyDir(cwd), rig.Services.Config.HierarchyDir(cwd));
            foreach (var team in teams)
            {
                _ = Directory.CreateDirectory(pool + "/progress/" + team);
                File.WriteAllLines(pool + "/progress/" + team + "/ledger.jsonl", big ? Enumerable.Range(0, 12).Select(BigRow) : [LedgerRow]);
            }
        }

        IReadOnlyList<DeciderBox> deciders = owner ? [.. teams.Select(t => new DeciderBox("orchestrator@" + t, t, "sess-1") { Dir = dir })] : [];
        var callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, null, null, null, deciders);
        var advisory = new AdvisoryServices(BindingAttestationTests.MailboxFor(rig), null!, callers, null!, null!, new SessionStartGuardTests.NoText(), null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory, Sessions = new HarnessSessions(), Process = rig.Services.Process with { ParentPid = HarnessSessions.Pid } };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = "sess-1", ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = source };

        var outcome = rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);

        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    [InlineData("compact")]
    [InlineData(Ah.Hooks.Lifecycle.SessionStart.OptInSource)]
    public void ASessionThatOwnsATeamGetsTheMemoryBlockOnEachRecallingSource(string source)
    {
        var context = Context(source, owner: true);

        Assert.Contains("AH MEMORY (tm)", context, StringComparison.Ordinal);
        Assert.Contains("use A", context, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionOwningTwoTeamsGetsBothSmallDigestsButOnlyAsManyBigOnesAsFitTheCap()
    {
        var small = Context("startup", owner: true, teams: ["tm", "tn"]);
        var big = Context("startup", owner: true, teams: ["tm", "tn"], big: true);

        Assert.Contains("AH MEMORY (tm)", small, StringComparison.Ordinal);
        Assert.Contains("AH MEMORY (tn)", small, StringComparison.Ordinal);
        Assert.Contains("AH MEMORY (tm)", big, StringComparison.Ordinal);
        Assert.DoesNotContain("AH MEMORY (tn)", big, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberOrASourceThatDoesNotRecallOrAnEmptyLedgerGetsNoBlock()
    {
        Assert.DoesNotContain("AH MEMORY", Context("resume", owner: false), StringComparison.Ordinal);
        Assert.DoesNotContain("AH MEMORY", Context("fork", owner: true), StringComparison.Ordinal);
        Assert.DoesNotContain("AH MEMORY", Context("resume", owner: true, withLedger: false), StringComparison.Ordinal);
    }
}
