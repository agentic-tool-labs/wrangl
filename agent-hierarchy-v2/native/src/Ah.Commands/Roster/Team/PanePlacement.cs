using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Where a pane goes when the caller names it: <c>--here</c> (the caller's own tab) or <c>--tab &lt;t&gt;</c>, each with an optional
/// <c>--split right|down</c>. <c>roster move</c> and the spawn verbs read the flags through here.
/// </summary>
internal static class PanePlacement
{
    /// <summary>The asked placement: the pane to split beside (null: any pane of <paramref name="Tab"/>) and the direction (null: by the pane's shape).</summary>
    internal sealed record Ask(string? Pane, string? Tab, string? Split);

    /// <summary><c>--split</c>'s value, refused unless it is <c>right</c> or <c>down</c>.</summary>
    internal static string SplitValue(RosterContext c, string verb)
    {
        var split = c.Opts.Text("split");
        if (split is not ("right" or "down"))
        {
            var shown = split is null ? "true" : JsonText.Quote(split);
            throw new RosterFailure($"{verb}: --split must be \"right\" or \"down\", got {shown}");
        }

        return split;
    }

    /// <summary>The spawn's <c>--here</c> / <c>--tab</c> placement, or null when neither is given (a bare <c>--split</c> keeps its old meaning).</summary>
    internal static Ask? Parse(RosterContext c, string verb)
    {
        var opts = c.Opts;
        var here = opts.IsTrue("here");
        if (!here && !opts.Has("tab"))
        {
            return null;
        }

        if (here && opts.Has("tab"))
        {
            throw new RosterFailure($"{verb}: use one of --here or --tab <id>, not both");
        }

        if (opts.Has("stream"))
        {
            throw new RosterFailure($"{verb}: --here and --tab cannot be combined with --stream; a stream places its members in its own tab");
        }

        var split = opts.Has("split") ? SplitValue(c, verb) : null;
        if (here)
        {
            var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText);
            return string.IsNullOrEmpty(pane) ? throw new RosterFailure($"{verb}: --here needs HERDR_PANE_ID (run it from a herdr pane)") : new Ask(pane, null, split);
        }

        return opts.Text("tab") is { Length: > 0 } tab ? new Ask(null, tab, split) : throw new RosterFailure($"{verb}: --tab requires a value");
    }

    /// <summary>Splits the asked pane (or the tab's first pane) and returns the new empty pane and its tab, for the launch to use as is.</summary>
    internal static (string Pane, string Tab) Split(RosterContext c, Ask ask, string verb, string cwd)
    {
        var herdr = c.Services.Launch!.Herdr;
        try
        {
            var target = ask.Pane ?? FirstPaneOf(herdr, ask.Tab!) ?? throw new RosterFailure($"{verb}: tab {ask.Tab} has no panes");
            var layout = herdr.Invoke(["pane", "layout", "--pane", target]).Member("result")?.Member("layout");
            var tab = ask.Tab ?? MemberPlanning.Text(layout?.Member("tab_id")) ?? throw new RosterFailure($"{verb}: herdr did not say which tab pane {target} is in");
            var direction = ask.Split ?? ByShape(layout, target);
            var made = herdr.Invoke(["pane", "split", "--pane", target, "--direction", direction, "--cwd", cwd, "--no-focus"]);
            return MemberPlanning.Text(made.Member("result")?.Member("pane")?.Member("pane_id")) is { Length: > 0 } pane
                ? (pane, tab)
                : throw new RosterFailure($"{verb}: herdr pane split returned no pane id");
        }
        catch (InvalidOperationException error)
        {
            throw new RosterFailure($"{verb}: could not place the pane: {error.Message} — nothing was launched");
        }
    }

    private static string? FirstPaneOf(Ah.Launch.IHerdr herdr, string tab) =>
        herdr.Invoke(["pane", "list"]).Member("result")?.Member("panes") is { IsArray: true } panes
            ? panes.AsArray().Where(p => MemberPlanning.Text(p.Member("tab_id")) == tab).Select(p => MemberPlanning.Text(p.Member("pane_id"))).FirstOrDefault()
            : null;

    // ponytail: "wide" = at least twice as many columns as rows (a cell is about twice as tall as wide); tune here if it splits the wrong way
    private static string ByShape(JsonValue? layout, string pane)
    {
        var rect = layout?.Member("panes") is { IsArray: true } panes
            ? panes.AsArray().Where(p => MemberPlanning.Text(p.Member("pane_id")) == pane).Select(p => p.Member("rect")).FirstOrDefault()
            : null;
        return rect is { IsMap: true } r && r.Member("width") is { IsNumber: true } w && r.Member("height") is { IsNumber: true } h && w.AsNumber() < 2 * h.AsNumber()
            ? "down"
            : "right";
    }
}
