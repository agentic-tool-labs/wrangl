using System.Text;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>An Orchestrator session that owns no team gets an empty team named after its folder at session start (ah2-34).</summary>
public sealed class ImplicitTeamTests
{
    private const string Session = "sess-1";

    private static (HooksHostTests.Rig Rig, string Dir, List<CallerRequest> Registered) Start(
        Action<HooksHostTests.Rig>? seed = null, string hook = "sessionstart", string hookEvent = "SessionStart")
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/agent-hierarchy.json", """{"version":1,"alwaysOrchestrator":true}""");
        seed?.Invoke(rig);
        var registered = new List<CallerRequest>();
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig), null!, new BindingAttestationTests.Callers([], registered.Add), null!, null!, new SessionStartGuardTests.NoText(), null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory, Sessions = new HarnessSessions(), Process = rig.Services.Process with { ParentPid = HarnessSessions.Pid } };
        var e = new JsonObject { ["hook_event_name"] = hookEvent, ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = "startup" };
        LastOutput = Encoding.UTF8.GetString(rig.Run(hook, Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
        return (rig, rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), registered);
    }

    // xunit runs one class's tests one at a time, so the last run's output can sit here.
    private static string LastOutput = string.Empty;

    /// <summary>A soft rule that threw let its call through; the next session start says so (b502).</summary>
    [Theory]
    [InlineData("pretooluse-soft-rules", true)]
    [InlineData("activity", false)]
    public void SessionStartSaysWhenASoftRuleFailedInTheLastDay(string hook, bool said)
    {
        _ = Start(rig =>
        {
            var log = rig.Services.Config.HookErrorLogPath;
            _ = Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, JsonText.Stringify(new JsonObject { ["ts"] = rig.Services.Clock.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ["hook"] = hook, ["message"] = "boom" }, 0) + "\n");
        });

        Assert.Contains("hook error(s) logged", LastOutput, StringComparison.Ordinal);
        Assert.Equal(said, LastOutput.Contains("a soft rule failed in the last 24 h", StringComparison.Ordinal));
    }

    private static List<JsonValue> ImplicitWrites(HooksHostTests.Rig rig) =>
        rig.Writer.Mutations
            .Select(m => JsonText.Parse(m))
            .Where(m => m.IsMap && m.Member("write") is { IsMap: true } write && write.Member("implicit") is { IsBool: true })
            .Select(m => m.Member("write")!.Value)
            .ToList();

    [Fact]
    public void ATeamlessOrchestratorGetsAnEmptyTeamNamedAfterItsFolderAndThenRegisters()
    {
        var (rig, _, registered) = Start();

        var team = Assert.Single(ImplicitWrites(rig));
        Assert.True(team.Member("implicit")!.Value.AsBool());
        Assert.Empty(team.Member("members")!.Value.AsArray());
        var owner = team.Member("orchestrator")!.Value;
        Assert.Equal(Session, owner.Member("session_id")!.Value.AsString());
        Assert.Equal(HarnessSessions.Pid, (int)owner.Member("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, owner.Member("pid_start")!.Value.AsString());
        Assert.NotEmpty(registered);
    }

    [Fact]
    public void AnOrchestratorWhoseSessionStartNeverRanGetsItsTeamAtItsNextPrompt()
    {
        var (rig, _, _) = Start(hook: "activity", hookEvent: "UserPromptSubmit");

        Assert.Equal(Session, Assert.Single(ImplicitWrites(rig)).Member("orchestrator")!.Value.Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void ATeamAlreadyHoldingTheFolderNameIsLeftAsItIs()
    {
        var (rig, _, _) = Start(r => HoldFixture.Orchestrates(r, "someone-else", "proj"));

        Assert.Empty(ImplicitWrites(rig));
    }
}
