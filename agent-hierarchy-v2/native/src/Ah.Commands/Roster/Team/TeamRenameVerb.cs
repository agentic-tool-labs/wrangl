using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>rename</c>: the team's display name, <c>display_name</c> on its record (ah2-218 A1, A2). A label only: nothing resolves a team by
/// it and it need not be unique. The user and the team's Orchestrator may set it (the guard of the user's own <c>quit</c>); a member
/// may not. The core is shared with <c>POST /ui/v1/team-rename</c>.
/// </summary>
internal static class TeamRenameVerb
{
    /// <summary>The most characters (Unicode scalar values) a display name has after trimming.</summary>
    internal const int MaxChars = 60;

    private static readonly string[] Flags = ["clear", "team", "cwd", "orchestrator-pid"];

    [Verb("roster:rename")]
    internal static int Rename(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        StreamSupport.RejectUnknownFlags(c, Flags, "rename");
        var clear = c.Opts.IsTrue("clear");
        if (clear == (c.Opts.Positionals.Count == 1) || c.Opts.Positionals.Count > 1)
        {
            throw new RosterFailure("rename: give one display name (quoted), or --clear; nothing was changed");
        }

        var input = new JsonObject
        {
            ["dir"] = c.Dir,
            ["team"] = c.TeamFile is { } t ? Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(c.Dir, t)) : string.Empty,
            ["display_name"] = clear ? JsonValue.Null : c.Opts.Positionals[0],
            ["by"] = c.UserActs ? "user" : "orchestrator",
        };
        var result = Apply(c.Services, input);
        if (result.Get("refused") is { IsMap: true } refused)
        {
            throw new RosterFailure($"rename: {refused.Member("detail")!.Value.AsString()}; nothing was changed");
        }

        c.Out(result);
        return 0;
    }

    /// <summary>Why <paramref name="raw"/> breaks A1, or null; <paramref name="value"/> is the trimmed text.</summary>
    internal static string? Invalid(string raw, out string value)
    {
        value = raw.Trim();
        if (value.Length == 0)
        {
            return "the display name is empty after trimming";
        }

        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            count++;
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return "the display name has a control character or a line break";
            }
        }

        return count > MaxChars ? $"the display name is {count} characters; at most {MaxChars}" : null;
    }

    /// <summary>
    /// The core both callers run: <c>{dir, team, display_name, by}</c>, <c>dir</c> resolved, <c>display_name</c> null to clear. Answers
    /// <c>{ok, team, display_name, previous}</c>, or <c>{refused: {reason, detail}}</c> with <c>invalid</c>, <c>unknown-team</c> or
    /// <c>write-failed</c> and nothing written. <c>by</c> is not stored: a team close records no history beyond its marker, so a rename records none either (A5).
    /// </summary>
    internal static JsonObject Apply(IRosterCliServices services, JsonObject input)
    {
        var dir = input.Get("dir") is { IsText: true } d ? d.AsString() : string.Empty;
        var team = input.Get("team") is { IsText: true } t ? t.AsString() : string.Empty;
        string? next = null;
        if (input.Get("display_name") is { IsText: true } raw)
        {
            if (Invalid(raw.AsString(), out var trimmed) is { } why)
            {
                return Refused("invalid", why);
            }

            next = trimmed;
        }

        var record = team.Length == 0 ? JsonValue.Null : services.Roster.ReadTeamRecord(dir, team);
        if (record.IsNull)
        {
            return Refused("unknown-team", team.Length == 0 ? "no team here; pass --team <name>" : $"no team {team}");
        }

        var previous = Of(record);
        string? read = previous;
        var written = services.Hierarchy.StateCalls.UpdateTeam(dir, team, doc =>
        {
            read = doc.IsMap ? Of(doc) : null;
            if (!doc.IsMap || read == next)
            {
                return null;
            }

            var copy = doc.AsObject().Clone();
            if (next is null)
            {
                copy.Remove("display_name");
            }
            else
            {
                copy["display_name"] = next;
            }

            return TeamFileOutcome.Write(copy);
        });
        if (!written.Result.Ok)
        {
            return Refused("write-failed", written.Result.Error ?? written.Result.ErrorCode ?? "the team record was not written");
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["team"] = team,
            ["display_name"] = next is null ? JsonValue.Null : next,
            ["previous"] = read is null ? JsonValue.Null : read,
        };
    }

    /// <summary>The record's display name: a non-blank text, trimmed; else null (absent, empty, or not text).</summary>
    internal static string? Of(JsonValue record) =>
        record.Member("display_name") is { IsText: true } v && v.AsString().Trim() is { Length: > 0 } s ? s : null;

    private static JsonObject Refused(string reason, string detail) => new() { ["refused"] = new JsonObject { ["reason"] = reason, ["detail"] = detail } };
}
