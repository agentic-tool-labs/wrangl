using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.DaemonClient;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// What SessionStart says about the herdr desktop mod that <c>ah herdr install</c> copied: one line when the version on line 1 of
/// its marker <c>mods/ah2/.ah2-owned</c> is older than the plugin. It only reads: nothing under the herdr config is written, and a
/// marker that is missing, unreadable, empty or not a version says nothing and never throws.
/// </summary>
internal static partial class HerdrModNotice
{
    private const string MarkerRel = "mods/ah2/.ah2-owned";

    /// <summary>The herdr desktop home: <c>$HERDR_APP_HOME</c> when set, else <c>~/.config/herdr-app</c>; null when it is set but not absolute.</summary>
    internal static string? Home(IAhEnvironment env)
    {
        var set = env.GetVariable("HERDR_APP_HOME");
        if (set is { Length: > 0 })
        {
            return Path.IsPathRooted(set) ? set : null;
        }

        return Path.Combine(env.HomeDirectory, ".config", "herdr-app");
    }

    /// <summary>The lines to add to the session context (zero or one).</summary>
    internal static IReadOnlyList<string> Lines(GateServices s, string pluginVersion)
    {
        try
        {
            if (Home(s.Environment) is not { } home)
            {
                return [];
            }

            var marker = NodePath.Join(home, MarkerRel);
            if (!s.Files.Exists(marker))
            {
                return [];
            }

            var installed = s.Files.ReadText(marker).Split('\n', 2)[0].TrimEnd('\r');
            if (!VersionLine().IsMatch(installed) || DaemonClientModule.CompareVersions(installed, pluginVersion) >= 0)
            {
                return [];
            }

            return [$"the Wrangl mod in herdr desktop is older than the plugin ({InjectedText.Clean(installed, 40)} < {InjectedText.Clean(pluginVersion, 40)}); run `ah herdr install` to update it"];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return [];
        }
    }

    // ponytail: "a version" = digits and dots, optional -prerelease / +build; the spec names no grammar.
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionLine();
}
