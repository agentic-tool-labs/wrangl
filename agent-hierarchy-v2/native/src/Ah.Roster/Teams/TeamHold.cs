using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Roster;

/// <summary>
/// A team's effective hold: the hold file that governs it (its own, or an ancestor's when it is a lane). <see cref="CascadeFrom"/> is
/// the ancestor team when the hold is inherited. <see cref="ParseError"/> is set when the file exists but cannot be read or parsed;
/// the team is then held as a <c>quit</c> (fail closed).
/// </summary>
/// <param name="Team">The team asked about.</param>
/// <param name="CascadeFrom">The ancestor team whose hold file governs, or null.</param>
/// <param name="Path">The hold file.</param>
/// <param name="Kind">The kind of hold: <c>quit</c> or <c>stop</c>.</param>
/// <param name="Phase">Where the hold stands: <c>winding-down</c>, <c>held</c> or <c>stopped</c>.</param>
/// <param name="Since">When the hold began, or null.</param>
/// <param name="Record">The parsed hold file; <see cref="JsonValue.Null"/> when it could not be parsed.</param>
/// <param name="ParseError">Why the file could not be read, or null.</param>
public sealed record EffectiveHold(
    string? Team,
    string? CascadeFrom,
    string Path,
    string Kind,
    string Phase,
    string? Since,
    JsonValue Record,
    string? ParseError)
{
    /// <summary>True while a hold-exempt message (a quit brief, a resume brief) is still delivered: winding down, or stopped.</summary>
    public bool AcceptsExempt => ParseError is null && Phase is "winding-down" or "stopped";

    /// <summary>The team whose hold file this is.</summary>
    public string? Source => CascadeFrom ?? Team;
}

/// <summary>Where a team's hold file lives and which teams one hold covers. The one place that knows the path.</summary>
internal static class TeamHoldFiles
{
    public const string DefaultTeamDir = "_default";

    public const string FileName = "hold.json";

    /// <summary>The code of the refusal every launch path gives for a held team; a failure text that starts with it is not a launch failure.</summary>
    public const string RefusalCode = "team_held";

    /// <summary><c>yyyyMMdd'T'HHmmssfff'Z'</c>, the stamp checkpoint records use.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string DirOf(string teamHome, string? team) =>
        NodePath.Join(teamHome, "holds", string.IsNullOrEmpty(team) ? DefaultTeamDir : team);

    public static string PathOf(string teamHome, string? team) => NodePath.Join(DirOf(teamHome, team), FileName);
}

internal sealed partial class RosterCore
{
    private const int HoldParentDepth = 8;

    public string HoldDir(string dir, string? team) => TeamHoldFiles.DirOf(TeamHomeDir(dir), team);

    public string HoldPath(string dir, string? team) => TeamHoldFiles.PathOf(TeamHomeDir(dir), team);

    public EffectiveHold? HoldOf(string dir, string? team)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var asked = string.IsNullOrEmpty(team) ? null : team;
        var current = asked;
        for (var depth = 0; depth <= HoldParentDepth; depth++)
        {
            if (ReadHoldFile(dir, current, asked, current == asked ? null : current) is { } found)
            {
                return found;
            }

            if (!seen.Add(current ?? string.Empty))
            {
                return null;
            }

            current = ParentTeamOf(dir, current);
            if (current is null)
            {
                return null;
            }
        }

        return null;
    }

    public IReadOnlyList<string?> HoldCovered(string dir, string? team)
    {
        var root = string.IsNullOrEmpty(team) ? null : team;
        var covered = new List<string?> { root };
        foreach (var name in ListTeamNames(dir))
        {
            if (name == root)
            {
                continue;
            }

            if (IsDescendantOf(dir, name, root))
            {
                covered.Add(name);
            }
        }

        return covered;
    }

    private bool IsDescendantOf(string dir, string name, string? ancestor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        var current = ParentTeamOf(dir, name);
        for (var depth = 0; depth < HoldParentDepth && current is not null; depth++)
        {
            if (current == ancestor)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                return false;
            }

            current = ParentTeamOf(dir, current);
        }

        return false;
    }

    private string? ParentTeamOf(string dir, string? team)
    {
        var record = ReadTeamRecord(dir, team);
        return !record.IsNull && record.Member("parent") is { IsMap: true } p && p.Member("team") is { IsText: true } t && t.AsString().Length > 0
            ? t.AsString()
            : null;
    }

    public EffectiveHold? OwnHold(string dir, string? team)
    {
        var asked = string.IsNullOrEmpty(team) ? null : team;
        return ReadHoldFile(dir, asked, asked, null);
    }

    public void ChangeHold(string dir, string? team, Func<string?, HoldWrite?> decide) =>
        (holds ?? throw new InvalidOperationException("the hold verbs need the state database, which this composition does not provide"))
            .Change(TeamHomeDir(dir), HoldTeamKey(team), decide);

    public MemberPause? PauseOf(string dir, string? team, string member) => holds?.PauseOf(TeamHomeDir(dir), HoldTeamKey(team), member);

    public void SetPause(string dir, string? team, string member, bool paused, long atMs) =>
        (holds ?? throw new InvalidOperationException("the member pause needs the state database, which this composition does not provide"))
            .SetPause(TeamHomeDir(dir), HoldTeamKey(team), member, paused, atMs);

    private static string HoldTeamKey(string? team) => string.IsNullOrEmpty(team) ? TeamHoldFiles.DefaultTeamDir : team;

    /// <summary>
    /// The team's own hold, from the holds table when this core has it (per-dir-files-to-db S11a-holds), else from its file. Its
    /// <see cref="EffectiveHold.Path"/> stays the old file path, the hold's key in every output.
    /// </summary>
    private EffectiveHold? ReadHoldFile(string dir, string? holder, string? asked, string? cascadeFrom)
    {
        var path = HoldPath(dir, holder);
        string text;
        if (holds is not null)
        {
            var read = holds.Read(TeamHomeDir(dir), HoldTeamKey(holder));
            if (read.Unreadable is { } why)
            {
                return Closed(path, asked, cascadeFrom, why);
            }

            if (read.Body is null)
            {
                return null;
            }

            text = read.Body;
        }
        else
        {
            if (!files.Exists(path))
            {
                return null;
            }

            try
            {
                text = files.ReadText(path);
            }
            catch (Exception e)
            {
                return files.Exists(path) ? Closed(path, asked, cascadeFrom, e.Message) : null;
            }
        }

        try
        {
            var doc = JsonText.Parse(text);
            if (!doc.IsMap)
            {
                return Closed(path, asked, cascadeFrom, "hold file is not a JSON object");
            }

            var kind = doc.Member("kind") is { IsText: true } k ? k.AsString() : string.Empty;
            if (kind is not ("quit" or "stop"))
            {
                return Closed(path, asked, cascadeFrom, $"hold file has no valid kind ('{kind}')");
            }

            var phase = doc.Member("phase") is { IsText: true } ph ? ph.AsString() : kind == "stop" ? "stopped" : "held";
            var since = doc.Member("since") is { IsText: true } s ? s.AsString() : null;
            return new EffectiveHold(asked, cascadeFrom, path, kind, phase, since, doc, null);
        }
        catch (Exception e)
        {
            return Closed(path, asked, cascadeFrom, e.Message);
        }
    }

    private static EffectiveHold Closed(string path, string? asked, string? cascadeFrom, string error) =>
        new(asked, cascadeFrom, path, "quit", "held", null, JsonValue.Null, error);
}
