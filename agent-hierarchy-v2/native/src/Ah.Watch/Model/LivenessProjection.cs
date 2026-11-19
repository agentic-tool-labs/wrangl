using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>
/// The part of a team summary's liveness that depends on the time of the response. The composed <c>member_states</c> says <c>unknown</c> for a member
/// nothing decides; here such a member whose last sign of life is absent or at least <see cref="LivenessRules.FreshSeconds"/> old becomes
/// <c>not_seen</c>, its <c>working</c> or <c>idle</c> state becomes <c>unknown</c> (an idle one only when the composer did not mark it
/// <see cref="Model.TeamSummaryComposer.LocatedKey"/>, which is never served), and <c>liveness_counts</c> tallies the four words from that one pass.
/// </summary>
internal static class LivenessProjection
{
    private const long FreshMs = LivenessRules.FreshSeconds * 1000L;

    /// <summary>Rewrites the <c>unknown</c> entries of <paramref name="body"/>'s <c>member_states</c> as of <paramref name="nowMs"/> and adds <c>liveness_counts</c>.</summary>
    public static void Project(JsonObject body, double nowMs)
    {
        var live = 0;
        var unknown = 0;
        var notSeen = 0;
        var dead = 0;
        var notWorking = 0;
        if (body.Get("member_states") is { IsMap: true } states)
        {
            foreach (var key in states.AsObject().Keys.ToList())
            {
                if (states.AsObject().Get(key) is not { IsMap: true } entry)
                {
                    continue;
                }

                var fields = entry.AsObject();
                var located = fields.Remove(Model.TeamSummaryComposer.LocatedKey);
                _ = fields.Remove(Model.TeamSummaryComposer.StuckAsUnknownKey);
                var word = fields.Str("liveness");
                if (word == "unknown" && (LastLifeMs(fields) is not { } life || nowMs - life >= FreshMs))
                {
                    word = "not_seen";
                    fields["liveness"] = word;
                    fields["process"] = Model.SummaryData.ProcessLine(word, fields.Str("state"));

                    // Its last known state is old news: it does not count as working, and neither does a plan item only it is on (below).
                    if (fields.Str("state") == "working")
                    {
                        fields["state"] = "unknown";
                        notWorking++;
                    }
                    else if (fields.Str("state") == "idle" && !located)
                    {
                        // ah2-242 C2: an idle member nothing locates reads No signal.
                        fields["state"] = "unknown";
                    }
                }

                switch (word)
                {
                    case "live":
                        live++;
                        break;
                    case "unknown":
                        unknown++;
                        break;
                    case "not_seen":
                        notSeen++;
                        break;
                    case "proven_dead":
                        dead++;
                        break;
                    default:
                        break;
                }
            }
        }

        // ah2-31: an item that counted as in progress only on the word of undecided members stops counting once none of them is seen any more,
        // so a team whose only working member went away goes quiet like any stale team. The list is composed-only and is never served.
        if (body.Get(Model.TeamSummaryComposer.UnseenWorkKey) is { IsArray: true } unseen)
        {
            _ = body.Remove(Model.TeamSummaryComposer.UnseenWorkKey);
            var gone = body.Get("member_states") is { IsMap: true } served
                ? unseen.AsArray().Count(item => item.IsArray && item.AsArray().All(k => k.IsText && served.AsObject().Get(k.AsString()) is { IsMap: true } m && m.AsObject().Str("liveness") == "not_seen"))
                : 0;
            if (gone > 0 && body.Get("in_progress_now") is { IsNumber: true } moving)
            {
                body["in_progress_now"] = JsonValue.FromNumber(Math.Max(0, moving.AsNumber() - gone));
            }
        }

        if (notWorking > 0 && body.Get("working") is { IsNumber: true } working)
        {
            body["working"] = JsonValue.FromNumber(Math.Max(0, working.AsNumber() - notWorking));
        }

        body["liveness_counts"] = JsonValue.FromObject(new JsonObject
        {
            ["live"] = JsonValue.FromNumber(live),
            ["unknown"] = JsonValue.FromNumber(unknown),
            ["not_seen"] = JsonValue.FromNumber(notSeen),
            ["proven_dead"] = JsonValue.FromNumber(dead),
        });
    }

    /// <summary>The first instant at which a member the composed summary calls <c>unknown</c> becomes <c>not_seen</c>, or null when none will.</summary>
    public static double? NextChange(JsonObject body)
    {
        double? next = null;
        if (body.Get("member_states") is { IsMap: true } states)
        {
            foreach (var key in states.AsObject().Keys)
            {
                if (states.AsObject().Get(key) is { IsMap: true } entry && entry.AsObject().Str("liveness") == "unknown" && LastLifeMs(entry.AsObject()) is { } life
                    && (next is null || life + FreshMs < next))
                {
                    next = life + FreshMs;
                }
            }
        }

        return next;
    }

    private static double? LastLifeMs(JsonObject entry) =>
        entry.Str("last_life_at") is { } text && Timestamps.TryParseMs(text, out var ms) ? ms : null;
}
