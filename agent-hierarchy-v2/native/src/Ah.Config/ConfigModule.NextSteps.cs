namespace Ah.Config;

public static partial class ConfigModule
{
    /// <summary>The events <c>ah rules next-step</c> knows a step for.</summary>
    public static IReadOnlyList<string> NextStepEvents() => NextSteps.Events;

    /// <summary>The step for <paramref name="eventName"/> with the built-in role labels and placeholders for a session's own values, or null when the table has no row for it.</summary>
    public static string? NextStepFor(string eventName) => NextSteps.StepFor(eventName);

    /// <summary>The step after an implement-class member reports DONE, with the session's own values filled in.</summary>
    public static string QaDispatchStep(string member, string slug, string msgCommand, string cwd) => NextSteps.QaDispatch(member, slug, msgCommand, cwd);
}
