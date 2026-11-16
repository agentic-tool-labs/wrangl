using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    [Fact]
    public void ASplitUnitWithNoHardTestKeepsEveryDenial()
    {
        var rig = StuckRig();
        var unit = new GateUnit("pretooluse-split", static _ => HookEmit.Deny("no"), GateClass.Split);
        var context = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, "auto"))), "pretooluse-split");

        Assert.True(IsDenied(HookHost.Advisorise(unit, context, unit.Body(context), rig.Errors)));
    }

    private static string AdvisoryInput(Rig rig, string? permissionMode, string eventName = "PreToolUse", string? command = null)
    {
        var input = new JsonObject
        {
            ["hook_event_name"] = eventName,
            ["tool_name"] = "Bash",
            ["tool_use_id"] = "tu-1",
            ["session_id"] = StuckSession,
            ["cwd"] = Path.Combine(rig.Sandbox.Root, "proj"),
            ["tool_input"] = new JsonObject { ["command"] = command ?? $"{rig.Sandbox.Root}/bin/{StuckDeniedCommand}" },
        };
        if (permissionMode is not null)
        {
            input["permission_mode"] = permissionMode;
        }

        return Encoding.UTF8.GetString(JsonText.ToUtf8(input, 0));
    }

    private static void WriteGateMode(Rig rig, string text)
    {
        var path = rig.Services.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static List<JsonValue> AdvisoryRows(Rig rig) =>
        [.. rig.Services.Hierarchy.Core.ReadPeerRecords().Where(static r => r.Member("type") is { IsText: true } t && t.AsString() == "advisory")];

    [Fact]
    public void AnAdvisoryStopBlockBecomesNoBlockWithContextAndARow()
    {
        var rig = StuckRig();
        var unit = new GateUnit("stop-peer-nudge", static _ => HookEmit.Block("keep going"), GateClass.Advisory);
        var context = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, "auto", "Stop"))), "stop-peer-nudge");

        var outcome = HookHost.Advisorise(unit, context, unit.Body(context), rig.Errors);

        var text = Text(outcome);
        Assert.DoesNotContain("\"decision\"", text, StringComparison.Ordinal);
        Assert.Contains("\"hookEventName\":\"Stop\"", text, StringComparison.Ordinal);
        Assert.Contains("ah advisory (stop-peer-nudge): keep going", text, StringComparison.Ordinal);
        Assert.Single(AdvisoryRows(rig));
    }

    [Fact]
    public void AStopBlockOutsideTheClassifierModesStillBlocksAndANonAdvisoryClassNeverConverts()
    {
        var rig = StuckRig();
        var advisory = new GateUnit("stop-peer-nudge", static _ => HookEmit.Block("keep going"), GateClass.Advisory);
        var split = new GateUnit("stop-peer-nudge", static _ => HookEmit.Block("keep going"), GateClass.Split);
        var bypass = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, "bypassPermissions", "Stop"))), "stop-peer-nudge");
        var auto = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, "auto", "Stop"))), "stop-peer-nudge");

        Assert.Contains("\"decision\":\"block\"", Text(HookHost.Advisorise(advisory, bypass, advisory.Body(bypass), rig.Errors)), StringComparison.Ordinal);
        Assert.Contains("\"decision\":\"block\"", Text(HookHost.Advisorise(split, auto, split.Body(auto), rig.Errors)), StringComparison.Ordinal);
        Assert.Empty(AdvisoryRows(rig));
    }

    private static HookOutcome RunUnit(Rig rig, GateUnit unit, string? permissionMode, TimeSpan? deadline = null) =>
        HookHost.Run(unit.Name, true, Encoding.UTF8.GetBytes(AdvisoryInput(rig, permissionMode)), rig.Errors, () => rig.Services, [unit], deadline);

    [Theory]
    [InlineData("auto", true)]
    [InlineData("default", true)]
    [InlineData("bypassPermissions", false)]
    [InlineData(null, false)]
    public void AnAdvisoryGateThatThrowsFailsOpenOnlyUnderTheClassifierAndLeavesARow(string? mode, bool open)
    {
        var rig = StuckRig();
        var unit = new GateUnit("pretooluse-boom", static _ => throw new InvalidOperationException("boom"), GateClass.Advisory);

        var outcome = RunUnit(rig, unit, mode);

        Assert.Equal(open, outcome.Stdout.IsEmpty);
        Assert.Equal(!open, IsDenied(outcome));
        Assert.NotEmpty(rig.Errors.Records);
    }

    [Fact]
    public void AGateOfAnyBlockingOrSplitClassThatThrowsStillFailsClosedUnderAuto()
    {
        foreach (var gateClass in new[] { GateClass.BlockA, GateClass.BlockB, GateClass.BlockC, GateClass.Split })
        {
            var rig = StuckRig();
            var unit = new GateUnit("pretooluse-boom", static _ => throw new InvalidOperationException("boom"), gateClass);

            Assert.True(IsDenied(RunUnit(rig, unit, "auto")), gateClass.ToString());
        }
    }

    [Fact]
    public void AnAdvisoryGateThatMissesItsDeadlineFailsOpenUnderAutoAndClosedOtherwise()
    {
        var rig = StuckRig();
        var slow = new GateUnit("pretooluse-slow", static _ => { Thread.Sleep(2000); return HookEmit.Silent; }, GateClass.Advisory);

        var open = RunUnit(rig, slow, "auto", TimeSpan.FromMilliseconds(100));
        var closed = RunUnit(rig, slow, "bypassPermissions", TimeSpan.FromMilliseconds(100));

        Assert.True(open.Stdout.IsEmpty);
        Assert.True(IsDenied(closed));
    }

    [Fact]
    public void AnAdvisoryGateThatThrowsUnderAnEnforceModeFailsClosed()
    {
        var rig = StuckRig();
        WriteGateMode(rig, "{\"gate_mode\":\"enforce\"}");
        var unit = new GateUnit("pretooluse-boom", static _ => throw new InvalidOperationException("boom"), GateClass.Advisory);

        Assert.True(IsDenied(RunUnit(rig, unit, "auto")));
    }

    [Fact]
    public void AnAdvisoryUnitThatDeniesNothingIsLeftAlone()
    {
        var rig = StuckRig();
        var unit = new GateUnit("pretooluse-ah-cli", static _ => HookEmit.Silent, GateClass.Advisory);
        var context = new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(AdvisoryInput(rig, "auto"))), "pretooluse-ah-cli");

        Assert.True(HookHost.Advisorise(unit, context, unit.Body(context), rig.Errors).Stdout.IsEmpty);
        Assert.Empty(AdvisoryRows(rig));
    }
}
