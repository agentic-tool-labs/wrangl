using System.Reflection;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>ah2-662: a launched member whose start hook was cut off before it registered and stamped its row is registered by its next hook.</summary>
public sealed class MemberRegistrationRetryTests
{
    private const string Member = "ah2-qa-1";

    private sealed record Scene(HooksHostTests.Rig Rig, string Cwd, List<CallerRequest> Registered);

    /// <summary>A member session in pane p1 whose team row for it holds <paramref name="rowStamp"/>; <paramref name="launched"/> exports the team file.</summary>
    private static Scene Build(string rowStamp = "", bool launched = true)
    {
        var home = UnitSandbox.Create();
        var teams = home.Home + "/.claude/ah2/hierarchy/proj/teams";
        var env = new Dictionary<string, string> { ["HOME"] = home.Home, ["AH_HOME"] = home.Root + "/ah", ["HERDR_PANE_ID"] = "p1" };
        if (launched)
        {
            env["WRANGL_TEAM_FILE"] = teams + "/mo.json";
        }

        var rig = new HooksHostTests.Rig(env, processTable: new HarnessSessions().Table);
        _ = Directory.CreateDirectory(teams);
        File.WriteAllText(
            teams + "/mo.json",
            "{\"team_id\":\"t-mo\",\"orchestrator\":{\"pid\":1,\"session_id\":\"o\"},\"members\":["
            + $"{{\"role\":\"qa\",\"name\":\"{Member}\",\"kind\":\"claude\",\"transport_id\":\"p1\"{rowStamp}}}]}}");
        var cwd = rig.Sandbox.Root + "/proj";
        _ = Directory.CreateDirectory(cwd + "/.claude/ah2");
        File.WriteAllText(cwd + "/.claude/ah2/agent-hierarchy.json", """{"version":1}""");
        return new Scene(rig, cwd, []);
    }

    private static void Fire(Scene scene, string eventName)
    {
        var rig = scene.Rig;
        var box = new CallerBox($"{Member}@mo", "/box", "mo", Member, "/mo.json");
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig),
            new StopPeerNudgeReportTests.FakeHooks(ReportState.Sent, mailed: true),
            new BindingAttestationTests.Callers([box], scene.Registered.Add),
            null!,
            null!,
            null!,
            DispatchProxy.Create<IHierarchyContext, ActivityAskTests.InertProxy>(),
            DispatchProxy.Create<IHierarchyRecovery, ActivityAskTests.InertProxy>(),
            new BindingAttestationTests.Daemon(),
            null!,
            null!);
        var services = rig.Services with { Advisory = advisory, Sessions = new HarnessSessions() };
        _ = rig.Run("activity", $$"""{"hook_event_name":"{{eventName}}","session_id":"sess-1","cwd":"{{scene.Cwd}}"}""", services: () => services);
    }

    /// <summary>The member rows of every team-file write that stamped a process on <see cref="Member"/>.</summary>
    private static List<JsonObject> Stamps(Scene scene) =>
        [.. scene.Rig.Writer.Mutations
            .Select(m => JsonText.Parse(m))
            .Where(m => m.IsMap && m.AsObject().Get("write") is { IsMap: true })
            .Select(m => m.AsObject().Get("write")!.Value.AsObject())
            .Where(w => w.Get("members") is { IsArray: true })
            .SelectMany(w => w.Get("members")!.Value.AsArray())
            .Select(r => r.AsObject())
            .Where(r => r.Get("name") is { IsText: true } n && n.AsString() == Member && r.Get("pid") is not null)];

    [Fact]
    public void ALaterHookOfAMemberWhoseRowHasAPaneAndNoStampRegistersAndStampsIt()
    {
        var scene = Build();

        Fire(scene, "Stop");

        Assert.NotEmpty(scene.Registered);
        var row = Assert.Single(Stamps(scene));
        Assert.Equal(HarnessSessions.Pid, row.Get("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, row.Get("pid_start")!.Value.AsString());
        Assert.Equal("sess-1", row.Get("session_id")!.Value.AsString());
    }

    [Fact]
    public void ARowAlreadyStampedWithASessionIsNotRegisteredOrStampedAgain()
    {
        var scene = Build(rowStamp: $",\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\",\"session_id\":\"sess-1\"");

        Fire(scene, "Stop");

        Assert.Empty(scene.Registered);
        Assert.Empty(Stamps(scene));
    }

    [Fact]
    public void ARowHeldByALiveOtherProcessIsNotRetriedSinceItsStampCannotLand()
    {
        var scene = Build(rowStamp: ",\"pid\":777,\"pid_start\":\"Fri Oct 16 08:00:00 2026\"");

        Fire(scene, "Stop");
        Fire(scene, "Stop");

        Assert.Empty(scene.Registered);
        Assert.Empty(Stamps(scene));
    }

    [Fact]
    public void ASessionNotLaunchedIntoATeamIsUntouchedEvenOnAMemberPane()
    {
        var scene = Build(launched: false);

        Fire(scene, "Stop");

        Assert.Empty(scene.Registered);
        Assert.Empty(Stamps(scene));
    }
}
