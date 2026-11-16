using Ah.Abstractions;
using Ah.Config;
using Ah.Platform;

namespace Ah.Cli.Composition;

/// <summary>
/// Whether the state home may be used, asked once at the entry of a hook or a verb and before anything is built over it. The
/// default home is always fine; any other home outside test mode must be one the user trusted from a terminal. The trust file is
/// read from the passwd-derived default home only, by the one trust store.
/// </summary>
internal static class AhHomeGate
{
    private const string InSessionAdvice = "Ask the user to trust this home from a terminal, or to unset AH_HOME.";

    /// <summary>The verdict for <paramref name="environment"/>. Never throws for a state the rule refuses.</summary>
    internal static AhHomeVerdict Check(IAhEnvironment environment, IFileReader files, uint? uid)
    {
        var trust = ConfigModule.CreateTrustStore(
            files,
            () => ConfigModule.TrustHome(environment),
            () => uid);
        return PlatformModule.CheckAhHome(environment, files, path => trust.IsTrusted(TrustFile.Homes, path));
    }

    /// <summary>The text a model may read: the reason and what the user does, with no command and no path built into one.</summary>
    internal static string InSession(AhHomeVerdict verdict) => $"ah: {verdict.Reason}, so ah did not run. {InSessionAdvice}";

    /// <summary>
    /// The text for a verb's standard error. A model can read standard error when it is not a terminal, so only a terminal gets the
    /// line that trusts the home, with the path control-stripped and capped; anything else gets the in-session text.
    /// </summary>
    internal static string ForStandardError(AhHomeVerdict verdict, string? configuredHome, bool standardErrorIsTerminal)
    {
        if (!standardErrorIsTerminal)
        {
            return InSession(verdict);
        }

        var text = $"ah: {verdict.Reason}, so ah did not run.";
        return configuredHome is { Length: > 0 } && configuredHome[0] == '/'
            ? $"{text}\nTrust it once, from this terminal:  ahd home trust {Ah.Core.Text.SafeText.Render(configuredHome)}\nor unset AH_HOME to use the default home."
            : $"{text}\nUnset AH_HOME to use the default home.";
    }
}
