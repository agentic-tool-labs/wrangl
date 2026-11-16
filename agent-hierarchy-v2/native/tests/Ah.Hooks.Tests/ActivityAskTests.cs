using System.Reflection;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks.Tests;

/// <summary>A lead whose session is in the recorder's member branch (a role row, or a checkout holding no team of its own) still parks on its live question; a member does not.</summary>
public sealed class ActivityAskTests
{
    /// <summary>Answers every call with the return type's default: this session owns no team and has no context reading.</summary>
    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.ReturnType is { IsValueType: true } value && value != typeof(void) ? Activator.CreateInstance(value) : null;
    }

    private static (HooksHostTests.Rig Rig, Func<string, HookOutcome> Fire) Arrange(string? role = null, string extra = "")
    {
        var rig = new HooksHostTests.Rig();
        var cwd = rig.Sandbox.Root + "/proj";
        var dir = rig.Services.Config.HierarchyDir(cwd);
        _ = Directory.CreateDirectory(dir);
        rig.PutActivity(dir, "s1", """{"activity":"working"}""");
        Assert.True(rig.Services.Hierarchy.Status.ReadActivityRecord(dir, "s1").IsTruthy);
        if (role is not null)
        {
            Ah.TestKit.TestHomeDocs.Put(rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, $"{{\"version\":1,\"sessions\":{{\"s1\":{{\"role\":\"{role}\"}}}}}}");
        }

        var services = rig.Services with
        {
            Advisory = new AdvisoryServices(null!, null!, null!, null!, null!, null!, DispatchProxy.Create<IHierarchyContext, InertProxy>(), DispatchProxy.Create<IHierarchyRecovery, InertProxy>(), null!, null!, null!),
        };
        HookOutcome Fire(string eventName) => rig.Run(
            "activity",
            $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"s1","cwd":"{{{cwd}}}",{{{extra}}}"tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Ship it?"},{"question":"Second"}]}}""",
            services: () => services);
        return (rig, Fire);
    }

    private static IEnumerable<JsonValue> Records(HooksHostTests.Rig rig) =>
        rig.Writer.Calls.Zip(rig.Writer.Arguments).Where(static c => c.First == "activity.record").Select(static c => JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(c.Second)));

    private static string? Text(JsonValue record, string name) => record.Member(name) is { IsText: true } t ? t.AsString() : null;

    [Theory]
    [InlineData(null)]
    [InlineData("orchestrator")]
    [InlineData("sub-orchestrator")]
    public void ALeadInTheMemberBranchParksOnItsQuestionAndTheAnswerReleasesIt(string? role)
    {
        var (rig, fire) = Arrange(role);

        fire("PreToolUse");

        Assert.Empty(rig.Errors.Records);
        var parked = Assert.Single(Records(rig));
        Assert.Equal("blocked", Text(parked, "activity"));
        Assert.Equal("question", Text(parked, "blocked_by"));
        Assert.Equal("Ship it?", Text(parked, "note"));

        fire("PostToolUse");

        var released = Records(rig).Last();
        Assert.Equal("working", Text(released, "activity"));
        Assert.Null(Text(released, "blocked_by"));
        Assert.Null(Text(released, "note"));
    }

    // b458: only a permission Notification marks a member blocked.
    [Fact]
    public void APermissionNotificationRecordsBlockedOnPermission()
    {
        var (rig, fire) = Arrange("implementor", "\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission\",");

        fire("Notification");

        var record = Assert.Single(Records(rig));
        Assert.Equal("blocked", Text(record, "activity"));
        Assert.Equal("permission", Text(record, "blocked_by"));
    }

    [Fact]
    public void NegativeAWaitingForInputNotificationRecordsNoBlock()
    {
        var (rig, fire) = Arrange("implementor", "\"notification_type\":\"idle_prompt\",\"message\":\"Claude is waiting for your input\",");

        fire("Notification");

        Assert.DoesNotContain(Records(rig), r => Text(r, "activity") == "blocked");
    }

    [Fact]
    public void NegativeAMembersDeniedQuestionDoesNotParkIt()
    {
        var (rig, fire) = Arrange("implementor");

        fire("PreToolUse");

        Assert.Empty(rig.Errors.Records);
        Assert.DoesNotContain(Records(rig), static r => Text(r, "activity") == "blocked");
    }
}
