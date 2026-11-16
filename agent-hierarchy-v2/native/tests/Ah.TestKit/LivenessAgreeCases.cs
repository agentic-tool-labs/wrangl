namespace Ah.TestKit;

/// <summary>A member's registration for an agreement case. The process it names, when running, shows the start <c>ps-&lt;pid&gt;</c>, which is also the start the registration recorded.</summary>
public sealed record AgreeRegistration(long Pid, bool Running);

/// <summary>The newest peers row for a member. A running pid shows the start <c>ps-&lt;pid&gt;</c>.</summary>
public sealed record AgreeRow(string Status, int AgeSeconds, long? Pid, string? PidStart, bool Running);

/// <summary>
/// One piece of evidence about a member and the liveness every surface must report for it. The read model and the view each run the same
/// table, so a surface that answers a row differently fails its own run.
/// </summary>
public sealed record LivenessAgreeCase(string Id, AgreeRegistration? Registration, AgreeRow? Row, bool ObserverSeesOthers, string Expected)
{
    /// <summary>The member the case is about when it has a registration.</summary>
    public const string Registered = "dev";

    /// <summary>The member the case is about when it has only a peers row, or nothing.</summary>
    public const string Unregistered = "qa";

    public string Member => Registration is null ? Unregistered : Registered;

    public static IReadOnlyList<LivenessAgreeCase> All { get; } =
    [
        new("AG1-registered-pid-alive", new(42, true), null, true, "live"),
        new("AG2-up-row-pid-and-start-alive", null, new("up", 60, 77, "ps-77", true), true, "live"),
        new("AG3-down-row-pid-gone-reference-seen", null, new("down", 60, 78, "ps-78", false), true, "proven_dead"),
        new("AG4-down-row-without-a-pid", null, new("down", 60, null, null, false), true, "unknown"),
        new("AG5-seen-ten-minutes-ago", null, new("seen", 600, null, null, false), true, "live"),
        new("AG6-seen-forty-minutes-ago", null, new("seen", 2400, null, null, false), true, "unknown"),
        new("AG7-no-row", null, null, true, "unknown"),
        new("AG8-registered-pid-gone-no-reference", new(42, false), null, false, "unknown"),
        new("AG3n-down-row-pid-still-running", null, new("down", 60, 78, "ps-78", true), true, "live"),
    ];
}
