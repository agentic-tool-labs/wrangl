using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The writes of the hold verbs: a team's hold and archived holds (database rows), and its plans and evidence (files in the team's
/// hold directory, through the atomic writer). Reads of the effective hold go through <c>IRosterCore.HoldOf</c>, never through here.
/// </summary>
internal static class HoldStore
{
    internal static IRawFileWriter Writer(RosterContext c) =>
        c.Services.Writer ?? throw new RosterFailure("the hold verbs need the file writer, which this composition does not provide");

    /// <summary>The team's own hold (not an ancestor's) as a map, or null when it is absent or cannot be read.</summary>
    internal static JsonObject? ReadOwn(RosterContext c, string? team) =>
        c.Services.Roster.OwnHold(c.Dir, team) is { ParseError: null, Record: { IsMap: true } map } ? map.AsObject() : null;

    /// <summary>Whether the team has a hold of its own that cannot be read (it stays held).</summary>
    internal static bool OwnUnreadable(RosterContext c, string? team) => c.Services.Roster.OwnHold(c.Dir, team) is { ParseError: not null };

    internal static string DirOf(RosterContext c, string? team) => c.Services.Roster.HoldDir(c.Dir, team);

    internal static string PathOf(RosterContext c, string? team) => c.Services.Roster.HoldPath(c.Dir, team);

    /// <summary>
    /// The one way a team's own hold is written: in one database transaction (per-dir-files-to-db S11a-holds, in place of the hold
    /// lock), <paramref name="decide"/> gets the hold as it is now (null when absent or unreadable) and returns the hold to write, or
    /// null to write nothing. It may throw to refuse. Returns what it returned. A change the database refuses (busy) exits 75 with
    /// nothing written.
    /// </summary>
    internal static JsonObject? Change(RosterContext c, string? team, Func<JsonObject?, JsonObject?> decide)
    {
        JsonObject? doc = null;
        InTransaction(c, team, body =>
        {
            doc = decide(Parsed(body));
            return doc is null ? null : new HoldWrite(JsonText.Stringify(doc, 2) + "\n");
        });
        return doc;
    }

    private static void InTransaction(RosterContext c, string? team, Func<string?, HoldWrite?> decide)
    {
        try
        {
            c.Services.Roster.ChangeHold(c.Dir, team, decide);
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure($"the hold of team {team ?? "default"} could not be changed; retry ({e.Message})", exitCode: 75);
        }
    }

    private static JsonObject? Parsed(string? body)
    {
        try
        {
            return body is not null && JsonText.Parse(body) is { IsMap: true } map ? map.AsObject() : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="current"/> is the hold the caller read earlier: present, with the same kind and the same <c>since</c>.</summary>
    internal static bool Same(JsonObject? current, JsonObject read) =>
        current is not null && Field(current, "kind") == Field(read, "kind") && Field(current, "since") == Field(read, "since");

    /// <summary>Writes <paramref name="doc"/> only when the hold on disk is still <paramref name="read"/>; false, with nothing written, when it was replaced or released.</summary>
    internal static bool Rewrite(RosterContext c, string? team, JsonObject read, JsonObject doc) =>
        Change(c, team, current => Same(current, read) ? doc : null) is not null;

    /// <summary>Archives the team's own hold in one transaction, only when it is still <paramref name="read"/>; returns the archive path, or null (nothing archived) when it was replaced or released.</summary>
    internal static string? Release(RosterContext c, string? team, JsonObject read)
    {
        string? archived = null;
        InTransaction(c, team, body =>
        {
            if (Parsed(body) is not { } doc || !Same(doc, read))
            {
                return null;
            }

            var (path, stamp, archive) = Archive(c, team, doc);
            archived = path;
            return new HoldWrite(null, stamp, archive);
        });
        return archived;
    }

    internal static void WriteFile(RosterContext c, string path, string content)
    {
        var writer = Writer(c);
        writer.Mkdir(NodePath.Dirname(path));
        writer.WriteAtomic(path, path + ".tmp", Utf8Text.Encode(content));
    }

    /// <summary>A path in the team's hold directory: <c>&lt;stamp&gt;-&lt;suffix&gt;</c>.</summary>
    internal static string Named(RosterContext c, string? team, string stamp, string suffix) => NodePath.Join(DirOf(c, team), $"{stamp}-{suffix}");

    internal static string StampOfSince(string? since) =>
        since is not null && DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? RosterModule.HoldStamp(at)
            : "unknown";

    internal static JsonObject By(RosterContext c, string? team)
    {
        var proved = c.Proved();
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
        return new JsonObject
        {
            ["pid"] = proved.Pid,
            ["session"] = proved.SessionId is null ? JsonValue.Null : proved.SessionId,
            ["role"] = !record.IsNull && record.Member("parent") is { IsMap: true } ? "sub-orchestrator" : "orchestrator",
        };
    }

    /// <summary>
    /// The archived hold, <c>resumed_at</c> and <c>resumed_by</c> added, for the <c>hold_archive</c> row written as its hold row is
    /// deleted, keyed by the since-stamp; a <c>hold.json</c> left from before the holds table goes. The path is the archive's key in
    /// every output (the old <c>&lt;since-stamp&gt;-hold.json</c> name); no file is written there.
    /// </summary>
    private static (string Path, string Stamp, string Body) Archive(RosterContext c, string? team, JsonObject doc)
    {
        var since = doc.Get("since") is { IsText: true } s ? s.AsString() : null;
        var archived = doc.Clone();
        archived["resumed_at"] = Ah.Core.Time.Timestamps.ToIsoString(c.Services.Clock.UtcNow);
        archived["resumed_by"] = By(c, team);
        var stamp = StampOfSince(since);
        if (c.Services.Files.Exists(PathOf(c, team)))
        {
            Writer(c).Unlink(PathOf(c, team));
        }

        return (Named(c, team, stamp, "hold.json"), stamp, JsonText.Stringify(archived, 2) + "\n");
    }

    /// <summary>The refusal every hold verb gives, in the shape of the other roster refusals plus the code.</summary>
    internal static RosterFailure Refuse(RosterContext c, string code, string message) =>
        c.Refuse(new JsonObject { ["refused"] = code, ["error"] = code, ["message"] = message }, message);

    /// <summary>The <c>--team</c> argument that names the team in a printed command.</summary>
    internal static string TeamArg(string? team) => team ?? "@default";

    /// <summary>The text of the text field <paramref name="name"/> of a hold document, or null.</summary>
    internal static string? Field(JsonObject doc, string name) => doc.Get(name) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;
}
