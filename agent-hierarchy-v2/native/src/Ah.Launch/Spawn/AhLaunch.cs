using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;

namespace Ah.Launch;

/// <summary>What a member's Claude command line is compared with: the member and the flags <see cref="SpawnShapes"/> would pass today.</summary>
/// <param name="Agent">The agent reference the launch passes to <c>--agent</c>.</param>
/// <param name="Name">The member's name: the member marker in <c>--settings</c>.</param>
/// <param name="TeamFile">The team file path the launch puts in <c>WRANGL_TEAM_FILE</c>.</param>
/// <param name="Model">The row's model; null, empty and <c>inherit</c> are not compared.</param>
/// <param name="Effort">The row's effort; null, empty and <c>inherit</c> are not compared.</param>
public sealed record AhLaunchExpectation(string Agent, string Name, string TeamFile, string? Model, string? Effort);

/// <summary>Whether a command line is a launch by ah of this member.</summary>
public enum AhLaunchVerdict
{
    /// <summary>The launch-fixed settings name this team file and this member, and agent, model and effort match.</summary>
    AhLaunch,

    /// <summary>The settings name this team file and this member, but agent, model or effort differ from what the row says today.</summary>
    Drift,

    /// <summary>The settings are absent, or name another team file or member: ah did not launch this process for this member.</summary>
    NoAhLaunch,
}

/// <summary>The verdict, and for a drift the line that says how the process differs from the row.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Drift">For <see cref="AhLaunchVerdict.Drift"/>, what the process runs and what the row says; otherwise null.</param>
public sealed record AhLaunchResult(AhLaunchVerdict Verdict, string? Drift);

/// <summary>
/// The one test of whether a Claude member's command line is a launch by ah. The identity is the <c>--settings</c> env, which the
/// launch builder fixes at launch and never rebuilds from the row or the config as they are later; agent, model and effort are
/// compared only to report drift.
/// </summary>
internal static class AhLaunch
{
    /// <summary>Classifies <paramref name="argv"/> (the process's arguments, one element each, the program first) against <paramref name="expected"/>.</summary>
    public static AhLaunchResult Classify(IReadOnlyList<string> argv, AhLaunchExpectation expected, Func<string, string?>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(expected);
        var named = SettingsEnv(argv, readFile) is { } env
            && env.Member("WRANGL_TEAM_FILE") is { IsText: true } team && SamePath(team.AsString(), expected.TeamFile)
            && env.Member(MeshModule.MemberMarkerEnv) is { IsText: true } member && member.AsString() == expected.Name;
        if (!named)
        {
            return new AhLaunchResult(AhLaunchVerdict.NoAhLaunch, null);
        }

        var differences = new List<string>();
        Compare(differences, "agent", FlagValue(argv, "--agent"), expected.Agent);
        Compare(differences, "model", FlagValue(argv, "--model"), expected.Model);
        Compare(differences, "effort", FlagValue(argv, "--effort"), expected.Effort);
        return differences.Count == 0
            ? new AhLaunchResult(AhLaunchVerdict.AhLaunch, null)
            : new AhLaunchResult(AhLaunchVerdict.Drift, $"{expected.Name} {string.Join(", ", differences)}; relaunch to apply");
    }

    private static void Compare(List<string> differences, string what, string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || expected == "inherit" || actual == expected)
        {
            return;
        }

        differences.Add($"runs {what} {actual ?? "(none)"}, row says {expected}");
    }

    /// <summary>
    /// Splits a <c>ps</c> command line into the words a process was started with: white space separates words, except that the
    /// JSON after <c>--settings</c> is one word however many spaces its paths hold. A listing that cannot say where a word ends
    /// is the reason to read a pane's argv from the multiplexer instead.
    /// </summary>
    public static IReadOnlyList<string> WordsOf(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var raw = commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>();
        for (var i = 0; i < raw.Length; i++)
        {
            words.Add(raw[i]);
            if (raw[i] != "--settings" || i + 1 >= raw.Length || !raw[i + 1].StartsWith('{'))
            {
                continue;
            }

            var json = raw[++i];
            while (Depth(json) > 0 && i + 1 < raw.Length)
            {
                json += " " + raw[++i];
            }

            words.Add(json);
        }

        return words;
    }

    private static string? FlagValue(IReadOnlyList<string> argv, string flag)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] == flag)
            {
                return argv[i + 1];
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="argv"/> names a settings file that cannot be read, so its identity is not known.</summary>
    public static bool SettingsFileUnread(IReadOnlyList<string> argv, Func<string, string?> readFile)
    {
        if (FlagValue(argv, "--settings") is not { } text || text.StartsWith('{'))
        {
            return false;
        }

        try
        {
            return readFile(text) is null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return true;
        }
    }

    private static JsonValue? SettingsEnv(IReadOnlyList<string> argv, Func<string, string?>? readFile)
    {
        if (FlagValue(argv, "--settings") is not { } text)
        {
            return null;
        }

        if (!text.StartsWith('{'))
        {
            // a launch-only settings file: its path is the value
            try
            {
                if (readFile?.Invoke(text) is not { } contents)
                {
                    return null;
                }

                text = contents;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return null;
            }
        }

        if (!JsonText.TryParse(Utf8Text.Encode(text), out var settings))
        {
            return null;
        }

        return settings.Member("env") is { IsMap: true } env ? env : (JsonValue?)null;
    }

    private static bool SamePath(string a, string b) => a == b || Path.GetFullPath(a) == Path.GetFullPath(b);

    private static int Depth(string json)
    {
        var depth = 0;
        foreach (var c in json)
        {
            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
        }

        return depth;
    }
}
