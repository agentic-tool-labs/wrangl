using Ah.Core.Json;

namespace Ah.Launch;

/// <summary>Where the next pane is split: the pane to split and the direction (<c>right</c> or <c>down</c>).</summary>
public sealed record SplitDecision(string? Target, string Direction);

/// <summary>
/// Why a layout loop stopped. <paramref name="Partial"/> is false when nothing had been created (the command fails with
/// <paramref name="Message"/>); true when some panes exist, and <paramref name="Payload"/> is the partial report to print
/// (<c>{panes, splits, mode, pane_count, complete: false, failed_at, attempted, error}</c>).
/// </summary>
public sealed record LayoutFailure(bool Partial, string Message, JsonObject? Payload);

/// <summary>The panes a layout loop made, in creation order, with the split taken for each; <paramref name="Failure"/> is null on success.</summary>
public sealed record LayoutLoopResult(IReadOnlyList<string> Panes, JsonArray Splits, LayoutFailure? Failure);

/// <summary>The pure layout rules: which pane to split next and in which direction, and the plan handed to the orchestrator.</summary>
public interface ILayoutRules
{
    /// <summary><c>auto</c> resolved once from the final target pane count: columns up to two panes, a grid beyond.</summary>
    string EffectiveMode(string mode, double paneCount);

    /// <summary>
    /// The greedy split-target rule. <paramref name="created"/> are the pane ids made so far, in creation order, and
    /// <paramref name="geometry"/> is <c>[{pane_id, rect: {width, height, x, y}}]</c>. <paramref name="self"/> is an ordinary candidate
    /// at every call, first in candidate order for ties. Throws <see cref="InvalidOperationException"/> for a candidate the geometry
    /// does not report. <paramref name="positions"/> false (herdr core: sizes but no positions) skips the grid band rule, which needs
    /// the root bounding box; the largest-area target, columns and the aspect rule still apply. A geometry entry with
    /// <c>size_known: false</c> makes that decision "right", from the largest candidate of known size, else the geometry's last pane.
    /// </summary>
    SplitDecision NextSplit(string mode, double paneCount, string self, IReadOnlyList<string> created, JsonArray geometry, bool positions = true);

    /// <summary>
    /// The plan-level herdr layout instructions for the orchestrator to drive, or null for a non-herdr transport or a roster with no
    /// pane member. <paramref name="members"/> are the plan's members; a member holds a pane when its route is <c>peer</c> or
    /// <c>pane</c>.
    /// </summary>
    JsonObject? LayoutPlan(string mode, string transport, IReadOnlyList<JsonValue> members, string cwd);
}

/// <summary>The layout rules.</summary>
public sealed record LayoutRules : ILayoutRules
{
    public string EffectiveMode(string mode, double paneCount)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (mode != "auto")
        {
            return mode;
        }

        return paneCount <= 2 ? "columns" : "grid";
    }

    public SplitDecision NextSplit(string mode, double paneCount, string self, IReadOnlyList<string> created, JsonArray geometry, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(geometry);
        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var unknownSize = new HashSet<string>(StringComparer.Ordinal);
        string? lastPane = null;
        foreach (var g in geometry)
        {
            if (g.Member("pane_id") is { IsText: true } id)
            {
                byId[id.AsString()] = g.Member("rect") ?? JsonValue.Null;
                lastPane = id.AsString();
                if (g.Member("size_known") is { IsBool: true } known && !known.AsBool())
                {
                    unknownSize.Add(id.AsString());
                }
            }
        }

        var candidates = new List<string>(created.Count + 1) { self };
        candidates.AddRange(created);

        string? target = null;
        var bestArea = -1.0;
        foreach (var id in candidates)
        {
            if (!byId.TryGetValue(id, out var rect) || !rect.IsTruthy)
            {
                throw new InvalidOperationException($"next-split: pane {JsonText.Stringify(id, 0)} is not present in the reported geometry");
            }

            var area = Num(rect, "width") * Num(rect, "height");
            if (area > bestArea && !unknownSize.Contains(id))
            {
                bestArea = area;
                target = id;
            }
        }

        // herdr core: a pane whose size is a placeholder never decides a split. Go right, from the largest known pane, else the tab's last.
        if (candidates.Any(unknownSize.Contains))
        {
            return new SplitDecision(target ?? lastPane, "right");
        }

        var targetRect = target is not null ? byId[target] : (JsonValue?)null;

        var total = paneCount + 1;
        var bands = Math.Max(total >= 3 ? 2 : 1, Math.Pow(2, Math.Floor(Math.Log2(total) / 2)));
        var cols = Math.Ceiling(total / bands);

        var rootX = double.PositiveInfinity;
        var rootY = double.PositiveInfinity;
        var rightEdge = double.NegativeInfinity;
        var bottomEdge = double.NegativeInfinity;
        foreach (var id in candidates)
        {
            var r = byId[id];
            rootX = MinJs(rootX, Num(r, "x"));
            rootY = MinJs(rootY, Num(r, "y"));
            rightEdge = MaxJs(rightEdge, Num(r, "x") + Num(r, "width"));
            bottomEdge = MaxJs(bottomEdge, Num(r, "y") + Num(r, "height"));
        }

        var rootWidth = rightEdge - rootX;
        var rootHeight = bottomEdge - rootY;

        string direction;
        if (EffectiveMode(mode, paneCount) == "columns")
        {
            direction = "right";
        }
        else
        {
            if (targetRect is null)
            {
                throw new InvalidOperationException($"Cannot read properties of undefined (reading '{(rootWidth > 0 && rootHeight > 0 ? "height" : "width")}')");
            }

            var height = Num(targetRect.Value, "height");
            var width = Num(targetRect.Value, "width");
            if (!positions)
            {
                direction = width > height * 2 ? "right" : "down";
            }
            else if (rootWidth > 0 && rootHeight > 0 && height * 2 * bands > rootHeight * 3)
            {
                direction = "down";
            }
            else if (rootWidth > 0 && rootHeight > 0 && width * 2 * cols > rootWidth * 3)
            {
                direction = "right";
            }
            else
            {
                direction = width > height * 2 ? "right" : "down";
            }
        }

        return new SplitDecision(target, direction);
    }

    public JsonObject? LayoutPlan(string mode, string transport, IReadOnlyList<JsonValue> members, string cwd)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (transport != "herdr")
        {
            return null;
        }

        var paneCount = members.Count(m => m.Member("route") is { IsText: true } route && route.AsString() is "peer" or "pane");
        if (paneCount == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["mode"] = mode,
            ["computed_by"] = "ah roster layout-splits",
            ["pane_count"] = paneCount,
            ["inspect_command"] = "herdr pane layout --current",
            ["inspect_source"] = new JsonObject { ["kind"] = "json", ["path"] = ".result.layout.panes" },
            ["split_command"] = $"herdr pane split --pane <SPLIT_TARGET> --direction <DIRECTION> --cwd \"{cwd}\" --no-focus",
            ["target_source"] = new JsonObject { ["kind"] = "json", ["path"] = ".result.pane.pane_id" },
        };
    }

    /// <summary>
    /// Whether a <c>pane layout</c> answer reports pane positions: false only when <c>.result.layout.positions</c> is <c>false</c>
    /// (herdr core); the TUI's answer has no such field.
    /// </summary>
    public static bool ReportsPositions(JsonValue layoutAnswer) =>
        layoutAnswer.Member("result")?.Member("layout")?.Member("positions") is not { IsBool: true } p || p.AsBool();

    /// <summary>A rect field as JavaScript arithmetic reads it: a missing or non-numeric field is NaN.</summary>
    private static double Num(JsonValue rect, string field) => JsConvert.ToNumber(rect.Member(field));

    private static double MinJs(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Min(a, b);

    private static double MaxJs(double a, double b) => double.IsNaN(a) || double.IsNaN(b) ? double.NaN : Math.Max(a, b);
}
