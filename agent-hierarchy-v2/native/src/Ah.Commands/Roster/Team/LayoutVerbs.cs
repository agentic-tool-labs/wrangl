using Ah.Core.Json;

namespace Ah.Commands;

/// <summary><c>next-split</c> and <c>layout-splits</c>: the greedy split-target rule, and the loop that applies it under herdr.</summary>
internal static class LayoutVerbs
{
    private static readonly string[] Modes = ["auto", "columns", "grid"];

    private static string Quote(string? text) => text is null ? "undefined" : JsonText.Quote(text);

    private static string Mode(RosterContext c)
    {
        var mode = c.Opts.Text("mode");
        if (mode is null || !Modes.Contains(mode))
        {
            var shown = c.Opts.IsTrue("mode") ? "true" : Quote(mode);
            throw new RosterFailure($"--mode must be one of {string.Join(", ", Modes)}, got {shown}");
        }

        return mode;
    }

    private static double PaneCount(RosterContext c, string verb)
    {
        var raw = c.Opts.Text("pane-count") ?? throw new RosterFailure($"{verb} needs --pane-count <N>");
        var n = JsConvert.ToNumber(JsonValue.FromString(raw));
        if (!RosterContext.IsInteger(n) || n < 0)
        {
            throw new RosterFailure($"--pane-count must be a non-negative integer, got {Quote(raw)}");
        }

        return n;
    }

    private static List<string> Created(JsonValue parsed) =>
        parsed.IsArray ? parsed.AsArray().Select(v => MemberPlanning.JsConvertText(v)).ToList() : [];

    internal static int NextSplit(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster next-split is not implemented natively: the launch layer is not composed");
            return 70;
        }

        var mode = Mode(c);
        var paneCount = PaneCount(c, "next-split");
        var self = c.Opts.Text("self") ?? throw new RosterFailure("next-split needs --self <pane-id>");
        var created = JsJson.Parse(c.Opts.Text("created") ?? throw new RosterFailure("next-split needs --created <json array>"));
        var geometry = JsJson.Parse(c.Opts.Text("geometry") ?? throw new RosterFailure("next-split needs --geometry <json array>"));
        var decision = c.Services.Launch.Layout.NextSplit(
            mode, paneCount, self, Created(created), geometry.IsArray ? geometry.AsArray() : new JsonArray());
        var result = new JsonObject();
        if (decision.Target is { } target)
        {
            result["target"] = target;
        }

        result["direction"] = decision.Direction;
        c.Out(result);
        return 0;
    }

    internal static int LayoutSplits(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster layout-splits is not implemented natively: the launch layer is not composed");
            return 70;
        }

        var launch = c.Services.Launch;
        var opts = c.Opts;
        if (opts.IsTrue("next") && opts.IsTrue("apply"))
        {
            throw new RosterFailure("--next and --apply are mutually exclusive");
        }

        if (launch.Transports.Detect() != "herdr")
        {
            throw new RosterFailure("layout-splits requires the herdr transport");
        }

        if (!launch.Herdr.OnPath())
        {
            throw new RosterFailure(
                "transport is herdr (HERDR_ENV=1) but no `herdr` binary is on PATH — cannot place panes. Install herdr, or unset HERDR_ENV to use tmux/terminal.");
        }

        var splitCwd = opts.Text("cwd") ?? c.Services.CurrentDirectory;
        if (opts.IsTrue("apply"))
        {
            var target = opts.Text("target") ?? throw new RosterFailure("layout-splits --apply needs --target <pane-id>");
            var direction = opts.Text("direction");
            if (direction is not ("right" or "down"))
            {
                var shown = opts.IsTrue("direction") ? "true" : Quote(direction);
                throw new RosterFailure($"--direction must be \"right\" or \"down\", got {shown}");
            }

            JsonValue split;
            try
            {
                split = launch.Herdr.Invoke(["pane", "split", "--pane", target, "--direction", direction, "--cwd", splitCwd, "--no-focus"]);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new RosterFailure(error.Message);
            }

            var paneId = split.Member("result")?.Member("pane")?.Member("pane_id")
                ?? throw new InvalidOperationException("Cannot read properties of undefined (reading 'pane_id')");
            c.Out(new JsonObject { ["pane_id"] = paneId, ["target"] = target, ["direction"] = direction });
            return 0;
        }

        var mode = Mode(c);
        var paneCount = PaneCount(c, "layout-splits");
        var self = opts.Text("self") ?? Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText)
            ?? throw new RosterFailure("layout-splits needs --self <pane-id> (or HERDR_PANE_ID in the environment)");
        if (opts.IsTrue("next"))
        {
            var created = JsJson.Parse(opts.Text("created") ?? throw new RosterFailure("layout-splits --next needs --created <json array>"));
            JsonValue layout;
            try
            {
                layout = launch.Herdr.Invoke(["pane", "layout", "--current"]);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new RosterFailure(error.Message);
            }

            var geometry = layout.Member("result")?.Member("layout")?.Member("panes") is { IsArray: true } panes
                ? panes.AsArray()
                : throw new InvalidOperationException("Cannot read properties of undefined (reading 'panes')");
            var createdIds = Created(created);
            var decision = launch.Layout.NextSplit(mode, paneCount, self, createdIds, geometry, Ah.Launch.LayoutRules.ReportsPositions(layout));
            var targetRect = geometry.FirstOrDefault(g => MemberPlanning.Text(g.Member("pane_id")) == decision.Target).Member("rect")
                ?? throw new InvalidOperationException("Cannot read properties of undefined (reading 'rect')");
            c.Out(new JsonObject
            {
                ["index"] = createdIds.Count + 1,
                ["pane_count"] = paneCount,
                ["mode"] = mode,
                ["target"] = RosterJson.Text(decision.Target),
                ["direction"] = decision.Direction,
                ["target_rect"] = targetRect,
                ["target_is_self"] = decision.Target == self,
                ["geometry"] = geometry,
            });
            return 0;
        }

        if (paneCount == 0)
        {
            c.Out(new JsonObject { ["panes"] = new JsonArray(), ["complete"] = true });
            return 0;
        }

        var loop = launch.Launcher.RunLayoutLoop(mode, (int)paneCount, self, splitCwd);
        if (loop.Failure is { } failure)
        {
            if (failure.Partial && failure.Payload is { } payload)
            {
                c.Out(payload);
                return 3;
            }

            throw new RosterFailure(failure.Message);
        }

        var made = new JsonArray();
        made.AddRange(loop.Panes.Select(p => (JsonValue)p));
        c.Out(new JsonObject { ["panes"] = made, ["splits"] = loop.Splits, ["mode"] = mode, ["pane_count"] = paneCount, ["complete"] = true });
        return 0;
    }
}
