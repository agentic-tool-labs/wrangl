using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Activity;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>The dismiss nudge: once per idle spell for a member the release check would close; never for a busy or held member.</summary>
public sealed class DismissNudgeTests
{
    private const string Session = "orch-1";

    private sealed class Setup
    {
        internal readonly HooksHostTests.Rig Rig = new();
        internal readonly List<string> Asked = [];
        internal readonly string Cwd;
        internal readonly string Dir;
        internal HashSet<string> Ready = new(StringComparer.Ordinal);
        internal bool Throws;

        internal Ah.TestKit.FakeClock Clock => (Ah.TestKit.FakeClock)Rig.Services.Clock;

        internal Setup()
        {
            Cwd = Rig.Sandbox.Root + "/proj";
            Dir = Rig.Services.Config.HierarchyDir(Cwd);
            Rig.Writer.Executing = [.. Rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "peer-records")];
            var registration = BindingAttestationTests.MailboxFor(Rig).RegistrationFile(Session);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
            File.WriteAllBytes(registration, JsonText.ToUtf8(
                new JsonObject { ["session_id"] = Session, ["address"] = "orchestrator@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["orchestrator@tm"] = Rig.Sandbox.Root + "/box" } }, 0));
            _ = Directory.CreateDirectory(Dir + "/teams");
            File.WriteAllBytes(Dir + "/teams/tm.json", JsonText.ToUtf8(new JsonObject
            {
                ["members"] = new JsonArray { new JsonObject { ["name"] = "tm-impl", ["session_id"] = "s-impl" } },
            }, 0));
        }

        internal void Activity(string activity, string since)
        {
            Rig.PutActivity(Dir, "s-impl", JsonText.Stringify(new JsonObject { ["activity"] = activity, ["state_since"] = since }, 0));
        }

        internal List<string> Nudge()
        {
            var services = Rig.Services with
            {
                Advisory = StopOrchestratorLivenessTests.Advisory(Rig, []),
                ReadyToDismiss = (_, _, _, names) =>
                {
                    Asked.AddRange(names);
                    return Throws ? throw new InvalidOperationException("git failed") : [.. names.Where(Ready.Contains)];
                },
            };
            var input = JsonText.Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = Session, ["cwd"] = Cwd, ["prompt"] = "go" }, 0))));
            return DismissNudge.Lines(new GateContext(services, input, "x"), services.Advisory!, Session);
        }
    }

    [Fact]
    public void AnIdleCleanMemberIsNudgedOncePerIdleSpell()
    {
        var s = new Setup { Ready = ["tm-impl"] };
        s.Activity("idle", "2026-10-11T11:00:00.000Z");
        var line = Assert.Single(s.Nudge());
        Assert.StartsWith("ah: tm-impl is idle and clean — dismiss it if its work is signed off: ", line, StringComparison.Ordinal);
        Assert.EndsWith(" dismiss tm-impl --cwd " + s.Cwd, line, StringComparison.Ordinal);
        Assert.DoesNotContain("--close", line, StringComparison.Ordinal);

        // the same spell: neither a line nor another check
        Assert.Empty(s.Nudge());
        Assert.Single(s.Asked);

        // a new idle spell is checked again
        s.Activity("idle", "2026-10-11T11:30:00.000Z");
        _ = Assert.Single(s.Nudge());
    }

    [Fact]
    public void ABusyMemberIsNeitherCheckedNorNudged()
    {
        var s = new Setup { Ready = ["tm-impl"] };
        s.Activity("working", "2026-10-11T11:00:00.000Z");
        Assert.Empty(s.Nudge());
        Assert.Empty(s.Asked);
    }

    [Fact]
    public void AHeldMemberThatIsCleanLaterInTheSameSpellIsNudgedOnceAfterTheRecheckWait()
    {
        // dirty, an open request or work ahead: the release check does not return it
        var s = new Setup();
        s.Activity("idle", "2026-10-11T11:00:00.000Z");
        Assert.Empty(s.Nudge());

        // inside five minutes: not checked again (nearest non-match of the throttle)
        s.Clock.UtcNow = s.Clock.UtcNow.AddMinutes(4);
        s.Ready = ["tm-impl"];
        Assert.Empty(s.Nudge());
        Assert.Single(s.Asked);

        s.Clock.UtcNow = s.Clock.UtcNow.AddMinutes(2);
        _ = Assert.Single(s.Nudge());
        s.Clock.UtcNow = s.Clock.UtcNow.AddMinutes(10);
        Assert.Empty(s.Nudge());
        Assert.Equal(2, s.Asked.Count);
    }

    [Fact]
    public void ACheckThatThrowsCountsAsACheckAndIsNotRetriedOnTheNextPrompt()
    {
        var s = new Setup { Throws = true };
        s.Activity("idle", "2026-10-11T11:00:00.000Z");
        Assert.Empty(s.Nudge());
        Assert.Empty(s.Nudge());
        Assert.Single(s.Asked);
    }

    [Fact]
    public void ASessionWithNoOrchestratorBoxIsNeverNudged()
    {
        var s = new Setup { Ready = ["tm-impl"] };
        s.Activity("idle", "2026-10-11T11:00:00.000Z");
        File.Delete(BindingAttestationTests.MailboxFor(s.Rig).RegistrationFile(Session));
        Assert.Empty(s.Nudge());
        Assert.Empty(s.Asked);
    }
}
