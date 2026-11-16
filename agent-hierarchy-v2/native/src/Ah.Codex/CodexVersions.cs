using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>A Codex version range the adapter was proven on: <c>Min</c> inclusive, <c>Max</c> exclusive.</summary>
public sealed record CodexRange(string Min, string Max);

/// <summary>A <c>codex --version</c> number, <c>X.Y.Z[-pre]</c>.</summary>
public sealed record CodexVersion(IReadOnlyList<double> Numbers, string? Pre, string Text);

/// <summary>Whether a version is inside the tested ranges; <c>Note</c> is the sentence a spawn prints and a member row keeps when it is not.</summary>
public sealed record AdapterGateResult(bool Ok, string? Version, string? Note);

/// <summary>The version gate: which Codex versions get the app-server adapter.</summary>
internal static partial class CodexVersions
{
    public static readonly IReadOnlyList<CodexRange> Tested = [new CodexRange("0.162.0-alpha.2", "0.162.0-alpha.3")];

    [GeneratedRegex(@"([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z.-]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^([0-9]+\.[0-9]+\.[0-9]+),([0-9]+\.[0-9]+\.[0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RangeOverridePattern();

    /// <summary>
    /// The ranges in force: <see cref="Tested"/>, except that a test sandbox may set <c>AH_CODEX_APPSERVER_RANGE</c> to
    /// <c>min,max</c> (so the in-range path can be exercised against a stub codex) or to <c>none</c>. Outside a test sandbox
    /// <see cref="IAhEnvironment.GetTestOnly"/> gives nothing and the override is ignored.
    /// </summary>
    public static IReadOnlyList<CodexRange> TestedRanges(IAhEnvironment environment)
    {
        var text = environment.GetTestOnly("AH_CODEX_APPSERVER_RANGE");
        if (text is null)
        {
            return Tested;
        }

        if (text == "none")
        {
            return [];
        }

        var m = RangeOverridePattern().Match(text);
        return m.Success ? [new CodexRange(m.Groups[1].Value, m.Groups[2].Value)] : Tested;
    }

    /// <summary><c>X.Y.Z[-pre]</c> out of <c>codex --version</c> text, or null.</summary>
    public static CodexVersion? ParseCodexVersion(string? text)
    {
        var m = VersionPattern().Match(text ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        double N(int group) => double.Parse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var pre = m.Groups[4].Success ? m.Groups[4].Value : null;
        return new CodexVersion([N(1), N(2), N(3)], pre, m.Value);
    }

    // A prerelease sorts before its release (semver); two prereleases compare as text.
    private static int Compare(CodexVersion a, CodexVersion b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a.Numbers[i] != b.Numbers[i])
            {
                return a.Numbers[i] < b.Numbers[i] ? -1 : 1;
            }
        }

        if (a.Pre == b.Pre)
        {
            return 0;
        }

        if (a.Pre is null)
        {
            return 1;
        }

        if (b.Pre is null)
        {
            return -1;
        }

        return string.CompareOrdinal(a.Pre, b.Pre) < 0 ? -1 : 1;
    }

    /// <summary>
    /// Whether <c>codex --version</c> output is inside <paramref name="ranges"/>. <c>Ok</c> is false for an unparseable version
    /// too. With no range at all the sentence ends at "adapter": there is no tested codex to be outside of.
    /// </summary>
    public static AdapterGateResult AdapterGate(string? versionText, IReadOnlyList<CodexRange> ranges)
    {
        var v = ParseCodexVersion(versionText);
        var shown = ranges.Count > 0 ? string.Join(" || ", ranges.Select(r => $">={r.Min} <{r.Max}")) : "(none yet)";
        if (v is not null && ranges.Any(r => Compare(v, ParseCodexVersion(r.Min)!) >= 0 && Compare(v, ParseCodexVersion(r.Max)!) < 0))
        {
            return new AdapterGateResult(true, v.Text, null);
        }

        if (ranges.Count == 0)
        {
            return new AdapterGateResult(
                false,
                v?.Text,
                $"codex {v?.Text ?? "(version unreadable)"} is outside the tested app-server range {shown}; launched without the app-server adapter");
        }

        return new AdapterGateResult(
            false,
            v?.Text,
            $"codex {v?.Text ?? "(version unknown)"} is outside the tested app-server range {shown}; launched without the app-server adapter (mail is pasted into its pane). Set adapter \"off\" to silence this.");
    }

    /// <summary>Whether a codex member on the herdr transport is to get the adapter, from its roster <c>adapter</c> field: <c>off</c> never, <c>appserver</c> always (the version gate then decides), absent only once a tested range exists.</summary>
    public static bool AdapterWanted(string? adapter, IReadOnlyList<CodexRange> ranges) => adapter switch
    {
        "off" => false,
        "appserver" => true,
        _ => ranges.Count > 0,
    };

    /// <summary>Whether a team row is a codex member that runs on its own app-server: it asked for the adapter, or it carries the pid of the server its spawn started, and it did not opt out.</summary>
    public static bool RowAdapted(JsonValue row)
    {
        if (!row.IsMap)
        {
            return false;
        }

        var obj = row.AsObject();
        var kind = obj.Get("kind");
        if (kind is not { IsText: true } || kind.Value.AsString() != "codex")
        {
            return false;
        }

        var adapter = obj.Get("adapter");
        var adapterText = adapter is { IsText: true } ? adapter.Value.AsString() : null;
        return adapterText != "off" && (adapterText == "appserver" || obj.ContainsKey("appserver_pid"));
    }
}
