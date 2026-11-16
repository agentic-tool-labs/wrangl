using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// What an Orchestrator's Stop owes for the questions it parked: each open, unpushed question of the session blocks one Stop with the push
/// to send, then never again (a <c>parked-push-nudge</c> gate row keyed on the question's id records that it was said).
/// </summary>
internal static class ParkedPushNudge
{
    private const string GateType = "parked-push-nudge";

    internal static string? Notice(GateContext c, AdvisoryServices advisory, string sessionId)
    {
        var s = c.Services;
        try
        {
            var dir = s.Config.HierarchyDir(c.Cwd());
            var asked = new HashSet<string>(StringComparer.Ordinal);
            _ = s.Hierarchy.Roster.HasGate(dir, g =>
            {
                if (g.Member("type") is { IsText: true } type && type.AsString() == GateType
                    && g.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId && g.Member("key") is { IsText: true } key)
                {
                    asked.Add(key.AsString());
                }

                return false;
            });
            var lines = new List<string>();
            foreach (var boxDir in advisory.Callers.SessionDeciderBoxes(c.Cwd(), sessionId).Select(b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var dialogs = AnsweredDialogs(s, boxDir, sessionId);
                foreach (var q in ParkedQuestions.Fold(AskStore.ReadStore(s, boxDir)).Where(q => q.Open && !q.Pushed && q.SessionId == sessionId))
                {
                    // Presence rule (ruling S7-R2): the user answered a dialog in this session, after the question was parked, that carried
                    // it, so a push is moot. The question stays open; only the nudge is silenced.
                    if (Timestamps.TryParseMs(q.At, out var parkedMs) && dialogs.Any(d => d.Ms > parkedMs && Carried(q, d.Questions)))
                    {
                        continue;
                    }

                    if (asked.Add(q.Id))
                    {
                        s.Hierarchy.StateCalls.AppendGate(dir, new JsonObject { ["type"] = GateType, ["session_id"] = sessionId, ["key"] = q.Id });
                        lines.Add(ParkedQuestions.PushNudgeReason(q));
                    }
                }
            }

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>
    /// Whether one of a dialog's <paramref name="questions"/> carried <paramref name="q"/> (ruling S7-R2 as narrowed): after
    /// <see cref="ParkedQuestions.NormaliseText"/>, the same header AND the same option-label set. Either alone repeats too often across unrelated
    /// questions. Unlike <see cref="ParkedQuestions.MatchAsked"/> the match need not be unique: this only silences.
    /// </summary>
    private static bool Carried(ParkedQuestion q, JsonValue questions)
    {
        static string Norm(JsonValue? v) => v is { IsText: true } t ? ParkedQuestions.NormaliseText(t.AsString()) : string.Empty;
        static HashSet<string> LabelSet(IEnumerable<string> labels) => [.. labels.Select(ParkedQuestions.NormaliseText).Where(static l => l.Length > 0)];
        var header = Norm(q.Question.Member("header"));
        var labels = LabelSet(q.Labels);
        return header.Length > 0 && labels.Count > 0 && questions.IsArray && questions.AsArray().Any(d =>
            Norm(d.Member("header")) == header
            && (d.Member("options") is { IsArray: true } options
                && LabelSet(options.AsArray().Select(static o => o.IsText ? o.AsString() : o.Member("label") is { IsText: true } l ? l.AsString() : null).OfType<string>()).SetEquals(labels)));
    }

    /// <summary>
    /// The AskUserQuestion dialogs <paramref name="sessionId"/> had answered, with when and what they asked, from the rows <c>posttooluse-ask-record</c>
    /// wrote to the asks log in <paramref name="dir"/>. A row with no answers, from another writer or another session, or unreadable, does not count.
    /// </summary>
    private static List<(double Ms, JsonValue Questions)> AnsweredDialogs(GateServices s, string dir, string sessionId)
    {
        var path = NodePath.Join(dir, "asks.jsonl");
        var dialogs = new List<(double Ms, JsonValue Questions)>();

        // per-dir-files-to-db §6c G5: one read; a log that cannot be read has none answered, so the nudge is not silenced
        if (!s.Files.ReadAskLog(path).TryLines(out var lines))
        {
            return dialogs;
        }

        foreach (var line in lines)
        {
            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (row.IsMap && row.Member("writer") is { IsText: true } writer && writer.AsString() == Ah.Config.ConfigModule.AskRecordWriter
                && row.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                && row.Member("response")?.Member("answers") is { IsMap: true } answers && answers.AsObject().Keys.Any()
                && row.Member("at") is { IsText: true } at && Timestamps.TryParseMs(at.AsString(), out var ms) && row.Member("questions") is { IsArray: true } questions)
            {
                dialogs.Add((ms, questions));
            }
        }

        return dialogs;
    }
}
