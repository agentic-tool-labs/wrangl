namespace Ah.Config;

/// <summary>
/// The one table of protocol steps that <c>ah rules next-step</c> prints, the review-loop lines of the directive and the Stop hook's
/// owed-QA text all read, so a step is written once. An event the directive states no step text for has no row.
/// </summary>
internal static class NextSteps
{
    public const string ImplementDone = "implement-done";

    public const string ReviewImplDefect = "review-impl-defect";

    public const string ReviewSpecDefect = "review-spec-defect";

    /// <summary>The role label <c>next-step</c> prints for the built-in review loop.</summary>
    public const string BuiltinImplementer = "Implementor";

    public const string BuiltinDesigner = "Architect";

    public static readonly IReadOnlyList<string> Events = [ImplementDone, ReviewImplDefect, ReviewSpecDefect];

    private const string QaDispatchTemplate =
        "`git merge --no-ff <branch of {member}>` into ah/ship/{slug} on the integration tip, then `{msg} new --type request --to qa --slug {slug} --title \"QA full at the {slug} merge\" --cwd {cwd}` with set `full` at that merge.";

    /// <summary>The step after an implement-class member reports DONE: merge its branch into the integration tip and file the QA request.</summary>
    public static string QaDispatch(string member, string slug, string msgCommand, string cwd) =>
        QaDispatchTemplate
            .Replace("{member}", member, StringComparison.Ordinal)
            .Replace("{slug}", slug, StringComparison.Ordinal)
            .Replace("{msg}", msgCommand, StringComparison.Ordinal)
            .Replace("{cwd}", cwd, StringComparison.Ordinal);

    /// <summary>The first half of the review-loop clause: where an implementation defect goes.</summary>
    public static string ImplDefectStep(string role) => $"Impl-defect → {role}";

    /// <summary>The second half of the review-loop clause: where a specification defect goes.</summary>
    public static string SpecDefectStep(string role) => $"spec-defect → {role}";

    /// <summary>The review-loop clause the directive prints, with the labels of the roles that own the rework.</summary>
    public static string ReviewClause(string implementer, string designer) => $"{ImplDefectStep(implementer)}; {SpecDefectStep(designer)}";

    /// <summary>
    /// The step for <paramref name="eventName"/> with the built-in role labels and placeholders where a session's own values go, or
    /// null when the table has no row for it.
    /// </summary>
    public static string? StepFor(string eventName) => eventName switch
    {
        ImplementDone => QaDispatch("implementing member", "<slug>", "ah msg", "<cwd>"),
        ReviewImplDefect => ImplDefectStep(BuiltinImplementer),
        ReviewSpecDefect => SpecDefectStep(BuiltinDesigner),
        _ => null,
    };
}
