using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The JSON form of the progress records the progress read ops return, and the reading of it back; both ends use this one codec.</summary>
internal static class ProgressJson
{
    private static JsonValue Opt(string? text) => text is null ? JsonValue.Null : JsonValue.FromString(text);

    private static JsonValue Time(DateTimeOffset? at) => at is { } t ? JsonValue.FromString(t.ToString("O", CultureInfo.InvariantCulture)) : JsonValue.Null;

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var list = new JsonArray();
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static string? Text(JsonValue v, string key) => v.Member(key) is { IsText: true } m ? m.AsString() : null;

    private static string Required(JsonValue v, string key) => Text(v, key) ?? throw new InvalidOperationException($"the progress record has no \"{key}\"");

    private static int Count(JsonValue v, string key) => v.Member(key) is { IsNumber: true } m ? (int)m.AsNumber() : 0;

    private static bool Flag(JsonValue v, string key) => v.Member(key) is { IsTruthy: true };

    private static DateTimeOffset? TimeOf(JsonValue v, string key) =>
        Text(v, key) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    private static IReadOnlyList<string> Strings(JsonValue v, string key) =>
        v.Member(key) is { IsArray: true } a ? [.. a.AsArray().Where(static x => x.IsText).Select(static x => x.AsString())] : [];

    /// <summary>The team's progress folder as the <c>progress.read</c> result.</summary>
    /// <param name="team">The folder as read.</param>
    /// <returns>The JSON object.</returns>
    internal static JsonValue ToJson(TeamProgress team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var streams = new JsonArray();
        foreach (var s in team.Streams)
        {
            streams.Add(new JsonObject
            {
                ["stream"] = s.Stream,
                ["has_checklist"] = s.HasChecklist,
                ["has_journal"] = s.HasJournal,
                ["done"] = s.Done,
                ["in_progress"] = s.InProgress,
                ["not_started"] = s.NotStarted,
                ["state"] = Opt(s.State),
                ["updated"] = Time(s.Updated),
                ["newest_entry"] = Time(s.NewestEntry),
                ["stale"] = s.Stale,
                ["newer_format"] = s.NewerFormat,
                ["warnings"] = Strings(s.Warnings),
                ["planned"] = s.Planned,
                ["title"] = Opt(s.Title),
            });
        }

        return new JsonObject
        {
            ["off"] = team.Off,
            ["rollup_present"] = team.RollupPresent,
            ["streams"] = streams,
            ["newer_format_files"] = Strings(team.NewerFormatFiles),
        };
    }

    /// <summary>Reads a <c>progress.read</c> result back.</summary>
    /// <param name="json">The result.</param>
    /// <returns>The team's progress.</returns>
    internal static TeamProgress ReadTeam(JsonValue json)
    {
        var streams = new List<StreamProgress>();
        if (json.Member("streams") is { IsArray: true } list)
        {
            foreach (var s in list.AsArray())
            {
                streams.Add(new StreamProgress(
                    Required(s, "stream"),
                    Flag(s, "has_checklist"),
                    Flag(s, "has_journal"),
                    Count(s, "done"),
                    Count(s, "in_progress"),
                    Count(s, "not_started"),
                    Text(s, "state"),
                    TimeOf(s, "updated"),
                    TimeOf(s, "newest_entry"),
                    Flag(s, "stale"),
                    Flag(s, "newer_format"),
                    Strings(s, "warnings"),
                    Flag(s, "planned"),
                    Text(s, "title")));
            }
        }

        return new TeamProgress(Flag(json, "off"), Flag(json, "rollup_present"), streams, Strings(json, "newer_format_files"));
    }

    /// <summary>A stream's folded plan as the <c>progress.plan</c> result.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="full">Every field of each item (the backlog), marked <c>full</c> so <see cref="ReadPlan"/> reads it back whole.</param>
    /// <returns>The JSON object.</returns>
    internal static JsonValue ToJson(PlanState plan, bool full = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var items = new JsonArray();
        if (full)
        {
            // every field of each item, as the backlog's doc column holds it
            foreach (var i in plan.Items)
            {
                items.Add(JsonText.Parse(Backlog.ToDoc(i)));
            }

            return new JsonObject { ["items"] = items, ["unknown_events"] = plan.UnknownEvents, ["full"] = true };
        }

        foreach (var i in plan.Items)
        {
            items.Add(new JsonObject
            {
                ["item"] = i.Item,
                ["title"] = i.Title,
                ["state"] = i.State,
                ["added_at"] = i.AddedAt,
                ["started_at"] = Opt(i.StartedAt),
                ["done_at"] = Opt(i.DoneAt),
                ["commits"] = Strings(i.Commits),
                ["branch"] = Opt(i.Branch),
                ["issue"] = i.Issue is { } n ? JsonValue.FromNumber(n) : JsonValue.Null,
                ["note"] = Opt(i.Note),
            });
        }

        return new JsonObject { ["items"] = items, ["unknown_events"] = plan.UnknownEvents };
    }

    /// <summary>Reads a <c>progress.plan</c> result back.</summary>
    /// <param name="json">The result.</param>
    /// <returns>The plan, or null when the stream has none.</returns>
    internal static PlanState? ReadPlan(JsonValue json)
    {
        if (!json.IsMap)
        {
            return null;
        }

        var items = new List<PlanItem>();
        if (json.Member("full") is { IsTruthy: true } && json.Member("items") is { IsArray: true } docs)
        {
            items.AddRange(docs.AsArray().Select(static d => Backlog.FromDoc(JsonText.Stringify(d, 0))).OfType<PlanItem>());
            return new PlanState(items, Count(json, "unknown_events"));
        }

        if (json.Member("items") is { IsArray: true } list)
        {
            foreach (var i in list.AsArray())
            {
                items.Add(new PlanItem(
                    Required(i, "item"),
                    Required(i, "title"),
                    Required(i, "state"),
                    Required(i, "added_at"),
                    Text(i, "started_at"),
                    Text(i, "done_at"),
                    Strings(i, "commits"),
                    Text(i, "branch"),
                    i.Member("issue") is { IsNumber: true } n ? (int)n.AsNumber() : null,
                    Text(i, "note")));
            }
        }

        return new PlanState(items, Count(json, "unknown_events"));
    }

    /// <summary>A stream's description as the <c>progress.describe</c> result.</summary>
    /// <param name="info">The description.</param>
    /// <returns>The JSON object.</returns>
    internal static JsonValue ToJson(ProgressStreamInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new JsonObject { ["owner"] = info.Owner, ["branch"] = info.Branch, ["work_tree"] = info.WorkTree, ["goal"] = Opt(info.Goal) };
    }

    /// <summary>Reads a <c>progress.describe</c> result back.</summary>
    /// <param name="json">The result.</param>
    /// <returns>The description.</returns>
    internal static ProgressStreamInfo ReadInfo(JsonValue json)
    {
        return new ProgressStreamInfo(Required(json, "owner"), Required(json, "branch"), Required(json, "work_tree"), Text(json, "goal"));
    }
}
