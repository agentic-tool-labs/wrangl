using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    public static TheoryData<string> CarvedCommands() =>
    [
        "roster create --parent mo --stream ws1",
        "roster spawn-one reviewer --team mo-ws1",
        "progress item add ws1 a --title A --team mo-ws1",
    ];

    private static void ArrangeSubOrchestrator(Rig rig, string soRole = "sub-orchestrator")
    {
        var dir = rig.Services.Config.HierarchyDir(Path.Combine(rig.Sandbox.Root, "proj"));
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(
            Path.Combine(dir, "teams", "mo.json"),
            "{\"version\":1,\"team_id\":\"t\",\"orchestrator\":{\"session_id\":\"sess-mo\",\"pid\":7,\"pid_start\":\"S\"},"
            + "\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"" + soRole + "\",\"route\":\"peer\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws1\"},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
        File.WriteAllText(
            Path.Combine(dir, "teams", "mo-ws1.json"),
            "{\"version\":1,\"team_id\":\"c\",\"orchestrator\":{\"session_id\":\"sess-so\",\"pid\":8},\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},\"streams\":{\"ws1\":{\"state\":\"open\"}}}");
        File.WriteAllText(
            Path.Combine(dir, "teams", "lone.json"),
            "{\"version\":1,\"team_id\":\"l\",\"orchestrator\":{\"session_id\":\"sess-so\",\"pid\":8},\"members\":[]}");
    }

    private static HookOutcome OwnerGate(Rig rig, string command, bool registered, bool subagent = false, string? agentType = null, Func<GateServices, GateServices>? tweak = null)
    {
        var cwd = Path.Combine(rig.Sandbox.Root, "proj");
        var dir = rig.Services.Config.HierarchyDir(cwd);
        var boxes = registered
            ? new[]
            {
                new CallerBox("mo-so-ws1@mo", "/ah/mailbox/x/so", "mo", "mo-so-ws1", Path.Combine(dir, "teams", "mo.json")),
                new CallerBox("orchestrator@mo-ws1", "/ah/mailbox/x/orchestrator", "mo-ws1", "orchestrator", Path.Combine(dir, "teams", "mo-ws1.json")),
            }
            : [];
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig),
            null!,
            new BindingAttestationTests.Callers(boxes),
            null!, null!, null!, null!, null!, null!, null!, null!);
        var services = rig.Services with { Advisory = advisory };
        if (tweak is not null)
        {
            services = tweak(services);
        }

        var input = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = "Bash",
            ["session_id"] = "sess-so",
            ["cwd"] = cwd,
            ["tool_input"] = new JsonObject { ["command"] = command },
        };
        if (subagent)
        {
            input["agent_id"] = "a-1";
        }

        if (agentType is not null)
        {
            input["agent_type"] = agentType;
        }

        return rig.Run("pretooluse-ah-cli", Encoding.UTF8.GetString(JsonText.ToUtf8(input, 0)), services: () => services);
    }

    private static Rig MarkedSubOrchestratorRig(string root, string soRole = "sub-orchestrator")
    {
        var rig = new Rig(new Dictionary<string, string> { [MeshModule.MemberMarkerEnv] = "mo-so-ws1" }, root);
        ArrangeSubOrchestrator(rig, soRole);
        return rig;
    }

    [Theory]
    [MemberData(nameof(CarvedCommands))]
    public void ARegisteredSubOrchestratorWithTheMemberMarkerIsNotRefusedForACarvedVerbOnItsOwnStream(string tail)
    {
        var root = MakePluginRoot();
        var rig = MarkedSubOrchestratorRig(root);

        var outcome = OwnerGate(rig, $"{root}/bin/ah {tail}", registered: true);

        Assert.False(IsDenied(outcome) && Reason(outcome) == MeshModule.OwnerVerbRefusal, tail);
    }

    [Theory]
    [InlineData("roster spawn-one --help")]
    [InlineData("roster spawn-ad-hoc --help")]
    [InlineData("roster spawn-one --help --cwd /x")]
    [InlineData("roster spawn-one --cwd /x --help")]
    public void ARegisteredCallerMayReadTheBareHelpOfACarvedVerb(string tail)
    {
        var root = MakePluginRoot();
        var rig = MarkedSubOrchestratorRig(root, soRole: "implementor");

        var outcome = OwnerGate(rig, $"{root}/bin/ah {tail}", registered: true);

        Assert.False(IsDenied(outcome), tail);
    }

    [Theory]
    [InlineData("roster spawn-one reviewer --team mo-ws1; ls")]
    [InlineData("roster spawn-one reviewer --team mo-ws1 && ls")]
    [InlineData("roster spawn-one --help; ls")]
    public void ACarvedVerbFollowedByACommandThatNamesNoOwnerVerbIsJudgedOnItsOwnSegment(string tail)
    {
        var root = MakePluginRoot();
        var rig = MarkedSubOrchestratorRig(root);

        var outcome = OwnerGate(rig, $"{root}/bin/ah {tail}", registered: true);

        Assert.False(IsDenied(outcome), tail);
    }
}
