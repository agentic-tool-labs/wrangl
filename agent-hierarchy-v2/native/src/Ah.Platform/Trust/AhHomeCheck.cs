using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Platform;

/// <summary>
/// The verdict on the state home. <see cref="Accepted"/> homes carry their <see cref="Home"/>: the resolved real path of what was
/// checked, which is what everything after the check uses, so a link retargeted afterwards cannot move a checked home. A refused
/// verdict carries the <see cref="Reason"/>, a short sentence without a command in it.
/// </summary>
public sealed record AhHomeVerdict(bool Accepted, string Home, string? Reason);

/// <summary>
/// An environment that resolves the state home once and keeps that answer: the verdict on the home and every later use of
/// <see cref="IAhEnvironment.AhHome"/> then name the same path.
/// </summary>
public interface IPinnedAhHome
{
    /// <summary>The resolved real path of the effective state home; null in test mode, for a relative path, one that cannot be resolved, or an environment that keeps none.</summary>
    string? ResolvedAhHome { get; }
}

/// <summary>
/// The rule for which state home outside test mode may be used: the default home under the passwd entry's home, or a home the
/// user trusted from a terminal. <c>HOME</c> is never an input. Test mode accepts what it was given.
/// </summary>
internal static class AhHomeCheck
{
    private const string DefaultUnderHome = Ah.Core.Text.AhPaths.HomeRelative;

    internal static AhHomeVerdict Evaluate(IAhEnvironment env, IFileReader files, Func<string, bool> isTrusted)
    {
        if (env.TestMode)
        {
            return new AhHomeVerdict(true, env.AhHome, null);
        }

        string passwd;
        try
        {
            passwd = env.HomeDirectory;
        }
        catch (InvalidOperationException)
        {
            return Refused("the passwd database has no home directory for this user");
        }

        var defaultHome = TestModePredicate.ResolveThroughLongestExistingPrefix(files, passwd.TrimEnd('/') + "/" + DefaultUnderHome);
        if (defaultHome is null)
        {
            return Refused("the default state home cannot be resolved");
        }

        var configured = env.GetVariable("AH_HOME") is { } raw ? JsText.Trim(raw) : string.Empty;
        if (configured.Length > 0 && configured[0] != '/')
        {
            return Refused("AH_HOME is not an absolute path");
        }

        // The environment's own resolution when it keeps one, so what is judged here is what AhHome returns afterwards.
        var resolved = (env as IPinnedAhHome)?.ResolvedAhHome ?? TestModePredicate.ResolveThroughLongestExistingPrefix(files, configured.Length == 0 ? defaultHome : configured);
        if (configured.Length == 0)
        {
            return new AhHomeVerdict(true, resolved ?? defaultHome, null);
        }

        if (resolved is null)
        {
            return Refused("AH_HOME cannot be resolved");
        }

        if (V1Refusal(files, passwd, resolved, "AH_HOME", "unset AH_HOME for Wrangl") is { } v1Reason)
        {
            return Refused(v1Reason);
        }

        return resolved == defaultHome || isTrusted(resolved)
            ? new AhHomeVerdict(true, resolved, null)
            : Refused("AH_HOME is not the default home and is not trusted");
    }

    /// <summary>
    /// Before any trust check: a trust-list entry, hand-edited or not, must never admit a path in ah v1's tree. Segment test on real
    /// paths, so a sibling that only starts with v1's name is not v1's. Null when <paramref name="resolved"/> is clear of it.
    /// </summary>
    private static string? V1Refusal(IFileReader files, string passwd, string resolved, string name, string fix)
    {
        var v1Home = TestModePredicate.ResolveThroughLongestExistingPrefix(files, Ah.Core.Text.V1Layout.HomeUnder(passwd.TrimEnd('/')));
        if (v1Home is null)
        {
            return "ah v1's home path (~/.claude/agent-hierarchy) cannot be resolved (a dangling link?)";
        }

        return Ah.Core.Text.V1Layout.IsAtOrUnder(v1Home, resolved) ? $"{name} is ah v1's home; Wrangl uses its own: {fix}" : null;
    }

    /// <summary>
    /// The state home regardless of <c>HOME</c> and <c>AH_HOME</c>: the default home under the passwd entry's home. No file an agent
    /// can write moves it.
    /// </summary>
    internal static AhHomeVerdict Canonical(IAhEnvironment env, IFileReader files)
    {
        string passwd;
        try
        {
            passwd = env.HomeDirectory;
        }
        catch (InvalidOperationException)
        {
            return Refused("the passwd database has no home directory for this user");
        }

        return TestModePredicate.ResolveThroughLongestExistingPrefix(files, passwd.TrimEnd('/') + "/" + DefaultUnderHome) is { } defaultHome
            ? new AhHomeVerdict(true, defaultHome, null)
            : Refused("the default state home cannot be resolved");
    }

    private static AhHomeVerdict Refused(string reason) => new(false, string.Empty, reason);
}

public static partial class PlatformModule
{
    /// <summary>
    /// Whether the state home in <paramref name="environment"/> may be used. Test mode accepts it. Otherwise it is accepted when
    /// it is absolute and resolves to the default home under the passwd entry's home, or <paramref name="isTrusted"/> says the
    /// user trusted the resolved path. A passwd entry that cannot be read refuses; <c>HOME</c> is never consulted.
    /// </summary>
    public static AhHomeVerdict CheckAhHome(IAhEnvironment environment, IFileReader files, Func<string, bool> isTrusted)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isTrusted);
        return AhHomeCheck.Evaluate(environment, files, isTrusted);
    }

    /// <summary>
    /// The default state home under the passwd entry's home, whatever <c>HOME</c> and <c>AH_HOME</c> say: the anchor for state a
    /// caller's environment must not be able to redirect.
    /// </summary>
    public static AhHomeVerdict CanonicalAhHome(IAhEnvironment environment, IFileReader files)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(files);
        return AhHomeCheck.Canonical(environment, files);
    }

    /// <summary><paramref name="path"/> resolved through its longest existing prefix, the form a home is compared in; null when it cannot be resolved.</summary>
    public static string? ResolveHomePath(IFileReader files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return TestModePredicate.ResolveThroughLongestExistingPrefix(files, path);
    }
}
