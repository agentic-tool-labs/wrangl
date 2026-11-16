using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Launch;

/// <summary>
/// The pure parts of team snapshots: the stamp in a snapshot's file name, the name a team goes by in it, the shape of a session id
/// a launch command may take, and what a resume does with each member of a snapshot. The questions that need the process list or
/// the mailbox registrations are answered by the caller through <c>aliveReason</c>.
/// </summary>
public interface ISnapshotRules
{
    /// <summary><c>YYYYMMDDTHHMMSSmmmZ</c> for <paramref name="instant"/>: the stamp in a snapshot's file name.</summary>
    string SnapshotStamp(DateTimeOffset instant);

    /// <summary>The name a team goes by in a snapshot file name: the named team, or <c>team</c> for the default team.</summary>
    string SnapshotTeamName(string? teamName);

    /// <summary>Whether <paramref name="value"/> is a Claude session id <c>claude --resume</c> may be given: it is interpolated into a launch command.</summary>
    bool IsSessionId(string value);

    /// <summary>
    /// What <c>resume</c> does with each member of snapshot <paramref name="doc"/>: <c>{name, role, model, kind, status, why?}</c>, status one
    /// of <c>skipped</c>, <c>no-worktree</c>, <c>alive</c>, <c>fresh</c> or <c>resume</c>. <paramref name="aliveReason"/> says why a session id
    /// counts as alive, or returns null when nothing alive holds it.
    /// </summary>
    JsonArray PlanMembers(JsonValue doc, Func<JsonValue?, string?> aliveReason);

    /// <summary>
    /// How many of the members to be launched run on a top-tier model: the cost line of the plan. <paramref name="tierOf"/> gives a model's
    /// tier (null when unknown) and <paramref name="topTier"/> is the tier of opus.
    /// </summary>
    int TopTierCount(JsonArray plan, Func<JsonValue?, int?> tierOf, int topTier);
}

/// <summary>The snapshot rules; <paramref name="KindDefault"/> is the kind of a member that names none.</summary>
public sealed record SnapshotRules(IFileReader Files, string KindDefault) : ISnapshotRules
{
    public string SnapshotStamp(DateTimeOffset instant)
    {
        var iso = Timestamps.ToIsoString(instant).Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal);
        var dot = iso.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? iso : iso.Remove(dot, 1);
    }

    public string SnapshotTeamName(string? teamName) => teamName ?? "team";

    public bool IsSessionId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 8 or > 128 || !IsAlnum(value[0]))
        {
            return false;
        }

        return value.All(c => IsAlnum(c) || c is '_' or '-');
    }

    private static bool IsAlnum(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    public JsonArray PlanMembers(JsonValue doc, Func<JsonValue?, string?> aliveReason)
    {
        ArgumentNullException.ThrowIfNull(aliveReason);
        if (doc.Member("members") is not { IsArray: true } members)
        {
            throw new InvalidOperationException("the snapshot has no members array");
        }

        var plan = new JsonArray();
        foreach (var m in members.AsArray())
        {
            if (m.IsNull)
            {
                throw new InvalidOperationException("cannot read a property of a null member");
            }

            var row = new JsonObject();
            if (m.Member("name") is { } name)
            {
                row["name"] = name;
            }

            if (m.Member("role") is { } role)
            {
                row["role"] = role;
            }

            row["model"] = m.Member("model") is { IsNull: false } model ? model : JsonValue.Null;
            JsonValue kind = m.Member("kind") is { IsNull: false } k ? k : KindDefault;
            row["kind"] = kind;

            JsonObject Planned(string status, string? why = null)
            {
                var o = row.Clone();
                o["status"] = status;
                if (why is not null)
                {
                    o["why"] = why;
                }

                return o;
            }

            var route = m.Member("route");
            if (!(route is { IsText: true } r && (r.AsString() == "peer" || r.AsString() == "pane")))
            {
                plan.Add(Planned("skipped", "no pane (subagent route): not resumable"));
                continue;
            }

            var root = m.Member("expected_root");
            if (!(root is { IsTruthy: true, IsText: true } rootValue && IsDirectory(rootValue.AsString())))
            {
                var shown = root is { IsTruthy: true } rv ? JsConvert.ToJsString(rv) : "(no expected_root)";
                plan.Add(Planned("no-worktree", $"{shown} does not exist"));
                continue;
            }

            var session = m.Member("session_id");
            if (aliveReason(session) is { } alive)
            {
                plan.Add(Planned("alive", alive));
                continue;
            }

            if (session is not { IsTruthy: true })
            {
                plan.Add(Planned("fresh", "no session id recorded"));
                continue;
            }

            if (!(kind.IsText && kind.AsString() == KindDefault))
            {
                plan.Add(Planned("fresh", $"kind {JsConvert.ToJsString(kind)}: resume is Claude-only for now"));
                continue;
            }

            if (!IsSessionId(JsConvert.ToJsString(session)))
            {
                plan.Add(Planned("fresh", "the recorded session id is not a usable id"));
                continue;
            }

            plan.Add(Planned("resume"));
        }

        return plan;
    }

    private bool IsDirectory(string path)
    {
        try
        {
            return Files.Stat(path) is { Kind: FileKind.Directory };
        }
        catch (Exception)
        {
            return false;
        }
    }

    public int TopTierCount(JsonArray plan, Func<JsonValue?, int?> tierOf, int topTier)
    {
        ArgumentNullException.ThrowIfNull(tierOf);
        var count = 0;
        foreach (var p in plan)
        {
            var status = p.Member("status") is { IsText: true } s ? s.AsString() : null;
            if (status is not ("resume" or "fresh"))
            {
                continue;
            }

            if (tierOf(p.Member("model")) is { } tier && tier >= topTier)
            {
                count++;
            }
        }

        return count;
    }
}
