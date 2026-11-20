namespace Ah.Watch;

/// <summary>A team member as the test-scope check judges it: its role name and the class that role has.</summary>
/// <param name="Role">The member's role, null when unknown.</param>
/// <param name="RoleClass">The role's class, or <see cref="WatchModule.UnknownRoleClass"/> when it cannot be determined.</param>
public sealed record TestCaller(string? Role, string? RoleClass);

/// <summary>The registry's layers and the rows it could not use.</summary>
/// <param name="Layers">The layers, highest first, with their usable row counts.</param>
/// <param name="Issues">The rows that were left out, and why.</param>
/// <param name="Adapters">One line per repository overlay row that adds environment or arguments to a test run: whether the repository is trusted (the row applies) or not (it is ignored).</param>
public sealed record TestRegistryInfo(IReadOnlyList<TestRegistryLayer> Layers, IReadOnlyList<TestRegistryIssue> Issues, IReadOnlyList<string> Adapters);

public static partial class WatchModule
{
    /// <summary>The role class a caller who is a team member but whose role cannot be determined is judged as; it is refused whole-project test runs.</summary>
    public const string UnknownRoleClass = "unknown";

    /// <summary>The built-in role that runs whole-project tests though its class is <c>implement</c>.</summary>
    public const string QaRole = "qa";

    /// <summary>The team member a grants lookup found: its role and class, or an unknown class when there is no record, role or class.</summary>
    public static TestCaller TestCallerOf(Ah.Config.MemberRoleInfo? found) => new(found?.Role, found?.Class ?? UnknownRoleClass);

    /// <summary>The refusal for <paramref name="command"/>; see <see cref="TestCaller"/> for who is judged.</summary>
    /// <remarks>
    /// Refused when the command holds a whole-project test run (including one already under the test bucket), the caller, asked for
    /// only then, is a team member of class <c>implement</c> or <see cref="UnknownRoleClass"/> whose role is not <see cref="QaRole"/>,
    /// and the default test bucket, asked for only then, is not idle (a token held or a ticket waiting). A null caller is not a team
    /// member; a null bucket is a limiter that is off.
    /// </remarks>
    public static string? TestScopeRefusal(string? home, string? cwd, string? command, Func<TestCaller?> caller, Func<BucketStatus?> bucket) =>
        ScopeRefusal(TestRegistry.For(home, cwd).MatchesIncludingBucketed(command), caller, bucket);

    /// <summary>As the overload taking the command text, for a command given as its words.</summary>
    public static string? TestScopeRefusal(string? home, string? cwd, IReadOnlyList<string> argv, Func<TestCaller?> caller, Func<BucketStatus?> bucket) =>
        ScopeRefusal(TestRegistry.For(home, cwd).MatchesOfWords(argv), caller, bucket);

    internal static string? ScopeRefusal(IReadOnlyList<TestMatch> matches, Func<TestCaller?> caller, Func<BucketStatus?> bucket)
    {
        var full = matches.FirstOrDefault(static m => m.Scope == TestScope.Full);
        if (full is null || caller() is not { RoleClass: "implement" or UnknownRoleClass, Role: not QaRole }
            || bucket() is not { } b || (b.HeldTokens == 0 && b.Waiting == 0))
        {
            return null;
        }

        var seg = full.Segment;
        var text = seg.ProgramName + (seg.Rest.Count > 0 ? " " + string.Join(' ', seg.Rest) : string.Empty);
        var noNarrow = full.Row.LightIf is null ? " This command has no narrow form; run the underlying test runner with a filter." : string.Empty;
        return FormattableString.Invariant($"ah: a whole-project test run by an implementor needs an idle test bucket ({b.HeldTokens}/{b.Capacity} tokens held, {b.Waiting} waiting) ({text}). Run a narrow form now: ah test run -- dotnet test <project> --filter <expr>; or retry when the bucket is idle; or report NEEDS-QA.{noNarrow}");
    }

    /// <summary>What the bucket does to the command <paramref name="argv"/>; see <see cref="TestPlan"/>.</summary>
    public static TestPlan PlanTestRun(string? home, string? cwd, IReadOnlyList<string> argv, int capacity, int? grant, Func<string, string?> env, Func<string, bool>? repoTrusted = null)
    {
        var registry = TestRegistry.For(home, cwd);
        return registry.Plan(argv, capacity, grant, env, registry.RepoDir is { } dir && repoTrusted?.Invoke(dir) == true);
    }

    /// <summary>The layers of the registry for a session in <paramref name="cwd"/> and the rows it could not use.</summary>
    public static TestRegistryInfo TestRegistryInfoFor(string? home, string? cwd, Func<string, bool>? repoTrusted = null)
    {
        var r = TestRegistry.For(home, cwd);
        var trusted = r.RepoDir is { } dir && repoTrusted?.Invoke(dir) == true;
        var adapters = r.RepoAdapterRows.Select(id => $"row {id}: {(trusted ? "applied: trusted repo" : "ignored: untrusted repo")} {r.RepoDir}").ToList();
        return new TestRegistryInfo(r.Layers, r.Issues, adapters);
    }

    /// <summary>Where the account stands against its rate limits, from the newest statusline sample under <paramref name="home"/>.</summary>
    public static UsageReading ReadUsage(string home, double nowMs) => UsageReader.Read(home, nowMs);
}
