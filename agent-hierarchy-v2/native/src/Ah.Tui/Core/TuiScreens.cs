using System.Globalization;
using Ah.Core.Json;

namespace Ah.Tui.Core;

/// <summary>A place on one screen: the row chosen, by id and by index for when that id goes, and how far the screen is scrolled.</summary>
internal sealed record TuiLevel(string? Id = null, int Index = 0, int Scroll = 0);

/// <summary>What the client remembers: the screen (0 teams, 1 one team, 2 one agent), its place on each, and what it shows.</summary>
internal sealed record TuiScreenState(int Depth, TuiLevel Teams, TuiLevel Agents, TuiLevel Items)
{
    internal static TuiScreenState Initial { get; } = new(0, new(), new(), new());

    internal bool Ended { get; init; }

    /// <summary>The Sitrep regions the viewer flipped from where they start, kept in memory only.</summary>
    internal IReadOnlySet<string> SitrepToggled { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The card of the work item whose dialog is open.</summary>
    internal string? Detail { get; init; }

    internal int DetailScroll { get; init; }

    internal bool Help { get; init; }

    /// <summary>The Sitrep's baseline for the team on show: what was on show when it last closed, kept in memory only.</summary>
    internal IReadOnlyDictionary<string, int>? Baseline { get; init; }

    /// <summary>The baseline kept for each team.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Seen { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
}

/// <summary>What a frame shows besides the client's state.</summary>
internal sealed record TuiScreenInput(IReadOnlyDictionary<string, JsonValue> Slices, DateTimeOffset Now, TimeZoneInfo Zone, bool Down = false, string Fault = "", DateTimeOffset? LastGood = null, Func<IEnumerable<string>, bool>? Answered = null);

internal enum TuiLineKind
{
    Normal,
    Title,
    Heading,
    Selected,
    Dim,
}

/// <summary>One line of a frame, plain; <paramref name="Tone"/> names the status tone of the span from <paramref name="ToneAt"/>.</summary>
internal sealed record TuiLine(string Text, TuiLineKind Kind = TuiLineKind.Normal, int ToneAt = 0, int ToneLength = 0, string? Tone = null);

/// <summary>Where an open dialog sits on screen, end exclusive; its close control is three cells at <paramref name="CloseX"/> on its top line.</summary>
internal sealed record TuiBox(int X0, int Y0, int X1, int Y1, int CloseX);

/// <summary>A frame of a screen: its lines, where clicks land, its stops and the body line of each, and the body's size.</summary>
internal sealed record TuiFrame(IReadOnlyList<TuiLine> Lines, Hit Hit, IReadOnlyList<string> RowIds, IReadOnlyList<int> RowLines, int BodyLines, int BodyHeight, TuiBox? Box = null)
{
    internal static TuiFrame Empty { get; } = new([], Hit.Empty, [], [], 0, 0);
}

/// <summary>
/// The three screens of the terminal client as pure functions of the slices, the client's state and a size: Teams; one team's agents and sitrep; one agent's work items.
/// A click and a key are the same move on the same state; every text is the daemon's, made safe to draw. Nothing here decides a state or a count.
/// </summary>
internal static class TuiScreens
{
    internal const string DownLine = "ahd is not running";
    internal const string Loading = "loading…";
    internal const string NoTeams = "No teams. Run /wrangl:hierarchy init in a repo.";
    internal const string AllEnded = "Every team has ended. e shows them.";
    internal const string NoItems = "Not holding a work item.";
    internal const string NoLog = "No log for this item.";
    internal const string NoItem = "This item is no longer listed.";

    private const int WheelRows = 3;
    private const string Sep = " › ";

    private static readonly string[] HelpPage =
    [
        "Keys",
        "  ↑↓ j k     move",
        "  home end   first, last",
        "  pgup pgdn  scroll a page",
        "  ⏎          open; on a sitrep heading, fold it",
        "  esc        back, or close a work item",
        "  e          ended teams, on Teams",
        "  r          read everything again",
        "  q ctrl-c   quit",
        string.Empty,
        "Mouse",
        "  click a row to open it, a sitrep heading to fold it, a crumb to go there; the wheel scrolls.",
        "  The terminal's own text selection needs Shift while the mouse is on.",
        string.Empty,
        "Everything else is in the web UI: ah view open.",
    ];

    /// <summary>The slice ids the state's screen reads, what is on show first, so a poll's budget never cuts it; then every team's record, for names and lanes.</summary>
    internal static List<string> Wanted(TuiScreenState s, IReadOnlyDictionary<string, JsonValue> slices, DateTimeOffset now)
    {
        var ids = new List<string>();
        if (s.Depth >= 1 && s.Teams.Id is { } team)
        {
            ids.AddRange(TeamSlices.TeamScreenIds(slices, team));
            if (s.Depth == 2)
            {
                ids.InsertRange(1, TeamSlices.PlanIds(slices, team));
            }

            if (s.Detail is { } card)
            {
                ids.InsertRange(1, TeamSlices.DetailIds(slices, card));
            }
        }

        ids.AddRange(TeamSlices.TeamIds(slices));
        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The state opened on the team of that name, or null when none has it.</summary>
    internal static TuiScreenState? OpenNamed(TuiScreenState s, IReadOnlyDictionary<string, JsonValue> slices, string name) =>
        TeamSlices.TeamIds(slices).Find(id => TeamSlices.NameOf(slices, id) == name) is { } id ? OpenTeam(s, id, 0) : null;

    private static TuiScreenState OpenTeam(TuiScreenState s, string id, int index) =>
        s with { Depth = 1, Teams = s.Teams with { Id = id, Index = index }, Agents = new(), Items = new(), Baseline = s.Seen.GetValueOrDefault(id) };

    /// <summary>The state with a chosen team or agent that has gone given up: one level up, on the nearest row.</summary>
    internal static TuiScreenState Settle(TuiScreenState s, IReadOnlyDictionary<string, JsonValue> slices)
    {
        if (s.Depth >= 1 && slices.ContainsKey("list:teams") && s.Teams.Id is { } team && !TeamSlices.TeamIds(slices).Contains(team))
        {
            return s with { Depth = 0, Teams = s.Teams with { Id = null }, Detail = null };
        }

        if (s.Depth == 2 && s.Teams.Id is { } mine && slices.ContainsKey(mine) && s.Agents.Id is { } agent
            && !TeamSlices.TeamScreenIds(slices, mine).Contains(agent))
        {
            return s with { Depth = 1, Agents = s.Agents with { Id = null }, Detail = null };
        }

        return s;
    }

    /// <summary>The frame of the state's screen. No <paramref name="rows"/> is one plain frame of everything, with no selection and no footer.</summary>
    internal static TuiFrame Frame(TuiScreenState s, TuiScreenInput input, int cols, int? rows)
    {
        if (input.Down)
        {
            return new TuiFrame([new TuiLine(TuiText.Fit(DownLine, Math.Max(1, cols)))], Hit.Empty, [], [], 0, 0);
        }

        if (s.Help && rows is not null)
        {
            return new TuiFrame([new TuiLine(TuiText.Fit("ah › keys", cols), TuiLineKind.Title), .. HelpPage.Select(l => new TuiLine(TuiText.Fit(l, cols)))], Hit.Empty, [], [], 0, 0);
        }

        var plain = rows is null;
        var page = s.Depth switch
        {
            0 => TeamsPage(s, input, cols, plain),
            1 => TeamPage(s, input, cols, plain),
            _ => AgentPage(s, input, cols, plain),
        };
        var frame = Assemble(page, s.Depth == 0 ? s.Teams : s.Depth == 1 ? s.Agents : s.Items, input, cols, rows);
        return s.Detail is { } card && rows is { } total ? WithDialog(frame, card, s.DetailScroll, input, cols, total) : frame;
    }

    private sealed record Page(List<(string Text, string Path)> Crumbs, string Right, List<TuiLine> Fixed, List<TuiLine> Body, List<string> RowIds, List<int> RowLines, string Footer);

    private static TuiFrame Assemble(Page p, TuiLevel level, TuiScreenInput input, int cols, int? rows)
    {
        var title = string.Join(Sep, p.Crumbs.Select(static c => c.Text));
        var hits = new List<HitCrumb>();
        var x = 0;
        foreach (var (text, path) in p.Crumbs)
        {
            var w = TuiText.Width(text);
            if (x < cols)
            {
                hits.Add(new HitCrumb(x, Math.Min(cols, x + w), path));
            }

            x += w + TuiText.Width(Sep);
        }

        var room = cols - TuiText.Width(title) - 2;
        var head = room >= 4 && p.Right.Length > 0
            ? title + new string(' ', Math.Max(2, cols - TuiText.Width(title) - Math.Min(room, TuiText.Width(p.Right)))) + TuiText.Fit(p.Right, room)
            : TuiText.Fit(title, cols);
        var lines = new List<TuiLine> { new(head, TuiLineKind.Title) };
        lines.AddRange(p.Fixed);
        var top = lines.Count;
        var hitRows = new List<HitRow>();
        var height = rows is { } r ? Math.Max(0, r - top - 1) : p.Body.Count;
        var scroll = Math.Clamp(level.Scroll, 0, Math.Max(0, p.Body.Count - height));
        lines.AddRange(p.Body.Skip(scroll).Take(height));
        for (var i = 0; i < p.RowLines.Count; i++)
        {
            if (p.RowLines[i] >= scroll && p.RowLines[i] < scroll + height)
            {
                hitRows.Add(new HitRow(top + p.RowLines[i] - scroll, i));
            }
        }

        if (rows is { } total)
        {
            while (lines.Count < total - 1)
            {
                lines.Add(new TuiLine(string.Empty));
            }

            var foot = input.Fault.Length > 0 ? TuiText.Plain(input.Fault) : p.Footer;
            if (total > 1)
            {
                lines.Add(new TuiLine(TuiText.Fit(foot, cols), TuiLineKind.Dim));
            }
        }

        return new TuiFrame(lines, new Hit(hitRows, hits), p.RowIds, p.RowLines, p.Body.Count, height);
    }

    /// <summary>The index of the row a level has chosen: its id, else the nearest to where it was; -1 with no rows.</summary>
    private static int Chosen(TuiLevel level, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return -1;
        }

        var at = level.Id is null ? -1 : ids.ToList().IndexOf(level.Id);
        return at >= 0 ? at : Math.Clamp(level.Index, 0, ids.Count - 1);
    }

    /// <summary>A row's cells at their widths, with the status cell's place: columns too wide for the screen have been dropped from the right already.</summary>
    private static TuiLine Row(string marker, IReadOnlyList<(string Text, int Width, bool Right)> cells, bool selected, int toneCell = -1, string? tone = null)
    {
        var text = marker;
        var (at, length) = (0, 0);
        for (var i = 0; i < cells.Count; i++)
        {
            var (cell, width, right) = cells[i];
            var fit = TuiText.Fit(cell, width);
            if (i == toneCell)
            {
                (at, length) = (text.Length, fit.Length);
            }

            text += (right ? TuiText.PadLeft(fit, width) : TuiText.Pad(fit, width)) + (i < cells.Count - 1 ? "  " : string.Empty);
        }

        return new TuiLine(text.TrimEnd(), selected ? TuiLineKind.Selected : TuiLineKind.Normal, at, length, selected ? null : tone);
    }

    /// <summary>The widths left of these columns, the first flexing into what remains of <paramref name="cols"/>; those on the right go until it has <paramref name="least"/>.</summary>
    private static int[] Widths(int cols, int least, params int[] rest)
    {
        var kept = rest.Length;
        while (kept > 0 && cols - rest.Take(kept).Sum(static w => w + 2) < least)
        {
            kept--;
        }

        return [Math.Max(1, cols - rest.Take(kept).Sum(static w => w + 2)), .. rest.Take(kept)];
    }

    private static string StateWord(StatusLanguage.Status status, string? since) => status.Glyph + " " + status.Word + (since is null ? string.Empty : " " + since);

    private static string Updated(TuiScreenInput input)
    {
        if (input.LastGood is not { } good)
        {
            return string.Empty;
        }

        var seconds = (int)Math.Max(0, (input.Now - good).TotalSeconds);
        return "updated " + (seconds < 60 ? seconds.ToString(CultureInfo.InvariantCulture) + "s" : AllTeams.Age(input.Now, good)) + " ago";
    }

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(static p => p.Length > 0));

    private static Page TeamsPage(TuiScreenState s, TuiScreenInput input, int cols, bool plain)
    {
        List<(string Text, string Path)> crumbs = [("ah", "0"), ("teams", "0")];
        var body = new List<TuiLine>();
        var ids = new List<string>();
        var lines = new List<int>();
        var fixedLines = new List<TuiLine>();
        const string Foot = "click or ⏎ open  e ended  ? keys  q quit";
        if (TeamSlices.Build(input.Slices, input.Now) is not { } tree)
        {
            body.Add(new TuiLine(Loading, TuiLineKind.Dim));
            return new Page(crumbs, Updated(input), fixedLines, body, ids, lines, Foot);
        }

        var shown = new List<(AllTeams.TeamRow Row, bool Lane)>();
        foreach (var top in tree.Model.Teams.Where(t => s.Ended || !t.Ended))
        {
            shown.Add((top, false));
            shown.AddRange((tree.Lanes.GetValueOrDefault(top.Path!) ?? []).Where(l => s.Ended || !l.Ended).Select(static l => (l, true)));
        }

        var live = tree.Own.Values.Count(static t => !t.Ended);
        var right = Join(live.ToString(CultureInfo.InvariantCulture) + " live", Updated(input));
        if (shown.Count == 0)
        {
            body.Add(new TuiLine(tree.Own.Count == 0 ? NoTeams : AllEnded, TuiLineKind.Dim));
            return new Page(crumbs, right, fixedLines, body, ids, lines, Foot);
        }

        var w = Widths(cols - 2, 14, 14, 5, 4, 7, 6, 8);
        (string Text, int Width, bool Right)[] Cells(params (string Text, bool Right)[] all) =>
            [.. all.Take(w.Length).Select((c, i) => (c.Text, w[i], c.Right))];
        fixedLines.Add(new TuiLine(Row("  ", Cells(("TEAM", false), ("STATE", false), ("NEEDS", true), ("WORK", true), ("BLOCKED", true), ("ITEMS", true), ("UPDATED", false)), false).Text, TuiLineKind.Heading));
        ids.AddRange(shown.Select(static t => t.Row.Path!));
        var chosen = plain ? -1 : Chosen(s.Teams, ids);
        for (var i = 0; i < shown.Count; i++)
        {
            var (row, lane) = shown[i];
            var needs = TeamSlices.NeedsShown(row.NeedsYou, !row.PlanLoaded) is { } n ? n.ToString(CultureInfo.InvariantCulture) : "–";
            lines.Add(body.Count);
            body.Add(Row(
                i == chosen ? "▸ " : "  ",
                Cells(
                    ((lane ? "  " : string.Empty) + TuiText.Plain(row.Name), false),
                    (StateWord(row.Status, row.Since), false),
                    (needs, true),
                    (row.Working.ToString(CultureInfo.InvariantCulture), true),
                    (row.Blocked.ToString(CultureInfo.InvariantCulture), true),
                    ($"{row.ItemsDone.ToString(CultureInfo.InvariantCulture)}/{row.ItemsTotal.ToString(CultureInfo.InvariantCulture)}", true),
                    (row.Updated ?? "–", false)),
                i == chosen,
                1,
                row.Status.Tone));
        }

        return new Page(crumbs, right, fixedLines, body, ids, lines, Foot);
    }

    private static Page TeamPage(TuiScreenState s, TuiScreenInput input, int cols, bool plain)
    {
        var id = s.Teams.Id ?? string.Empty;
        var slices = input.Slices;
        var name = id.Length > 0 ? TuiText.Plain(TeamSlices.NameOf(slices, id)) : string.Empty;
        List<(string Text, string Path)> crumbs = [("ah", "0"), (name, "1")];
        var body = new List<TuiLine>();
        var ids = new List<string>();
        var lines = new List<int>();
        var tree = TeamSlices.Build(slices, input.Now);
        var detail = tree is null ? null : TeamSlices.Team(tree, slices, id, input.Now);
        var agents = slices.ContainsKey(id) && detail is { } d ? d.Detail.Agents : null;
        body.Add(new TuiLine("AGENTS", TuiLineKind.Heading));
        if (TeamSlices.DutyLine(slices, id) is { } onDuty)
        {
            body.Add(new TuiLine(TuiText.Fit("  " + TuiText.Plain(onDuty), cols), TuiLineKind.Dim));
        }

        var lane = TeamSlices.IsLane(slices, id);
        var sitrep = lane ? null : TeamSlices.Sitrep(slices, id, s.Baseline, input.Now);
        var stack = sitrep is null ? [] : SitrepView.StackedLines(sitrep, s.SitrepToggled);
        ids.AddRange(agents?.Select(static a => a.Id) ?? []);
        var agentCount = ids.Count;
        ids.AddRange(stack.Select(static l => l.Stop).OfType<string>());
        var chosen = plain ? -1 : Chosen(s.Agents, ids);
        if (agents is null)
        {
            body.Add(new TuiLine(Loading, TuiLineKind.Dim));
        }
        else
        {
            var widths = AgentWidths(cols - 2);
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                string[] all = [TuiText.Plain(a.Short ?? a.Name), StateWord(a.Status, a.Since), a.Live, TuiText.Plain(a.Item ?? a.Working)];
                lines.Add(body.Count);
                body.Add(Row(i == chosen ? "▸ " : "  ", [.. widths.Select((width, c) => (all[c], width, false))], i == chosen, widths.Length > 1 ? 1 : -1, a.Status.Tone));
            }
        }

        var forOrchestrator = id.Length > 0 ? TeamSlices.OrchestratorAlerts(slices, id, input.Now) : [];
        if (forOrchestrator.Count > 0)
        {
            body.Add(new TuiLine("FOR THE ORCHESTRATOR · NOT COUNTED", TuiLineKind.Heading));
            body.AddRange(forOrchestrator.Select(a => new TuiLine(TuiText.Fit("  " + TuiText.Plain(a), cols), TuiLineKind.Dim)));
        }

        body.Add(new TuiLine("SITREP", TuiLineKind.Heading));
        if (lane)
        {
            body.Add(new TuiLine(SitrepView.PerTopLevelTeam, TuiLineKind.Dim));
        }
        else if (sitrep is null)
        {
            body.Add(new TuiLine(Loading, TuiLineKind.Dim));
        }

        var stop = agentCount;
        foreach (var l in stack)
        {
            var here = l.Stop is not null && stop == chosen;
            if (l.Stop is not null)
            {
                lines.Add(body.Count);
                stop++;
            }

            body.Add(new TuiLine(TuiText.Fit((here ? "▸ " : "  ") + (l.Heading ? string.Empty : "  ") + l.Text, cols), here ? TuiLineKind.Selected : l.Heading ? TuiLineKind.Heading : l.Stop is null ? TuiLineKind.Dim : TuiLineKind.Normal));
        }

        var counts = TeamCounts(tree, id);
        var left = sitrep is { Left: > 0 } gone ? gone.Left.ToString(CultureInfo.InvariantCulture) + " left the view" : string.Empty;
        return new Page(crumbs, Join(counts, Updated(input), left), [], body, ids, lines, "click or ⏎ open or fold  esc teams  q quit");
    }

    /// <summary>The name, state, liveness and item columns that fit, the rightmost going first; the item takes what is left.</summary>
    private static int[] AgentWidths(int total)
    {
        const int Name = 14;
        const int State = 18;
        const int Live = 6;
        var item = total - Name - State - Live - 6;
        return item >= 8 ? [Name, State, Live, item]
            : total >= Name + State + Live + 4 ? [Name, State, Live]
            : total >= Name + State + 2 ? [Name, State]
            : [Math.Max(1, total)];
    }

    /// <summary>The team's own counts for the header, in the Sitrep's glyphs.</summary>
    private static string TeamCounts(TeamSlices.Tree? tree, string id)
    {
        if (tree is null || !tree.Own.TryGetValue(id, out var row))
        {
            return string.Empty;
        }

        var needs = TeamSlices.NeedsShown(row.NeedsYou, !row.PlanLoaded) is { } n ? n.ToString(CultureInfo.InvariantCulture) : "–";
        return $"?{needs} ▶{row.Working.ToString(CultureInfo.InvariantCulture)} ‖{row.Blocked.ToString(CultureInfo.InvariantCulture)}";
    }

    private static Page AgentPage(TuiScreenState s, TuiScreenInput input, int cols, bool plain)
    {
        var team = s.Teams.Id ?? string.Empty;
        var member = s.Agents.Id ?? string.Empty;
        var slices = input.Slices;
        var tree = TeamSlices.Build(slices, input.Now);
        var agent = tree is null ? null : TeamSlices.Team(tree, slices, team, input.Now)?.Detail.Agents.FirstOrDefault(a => a.Id == member);
        var name = agent is { } a ? TuiText.Plain(a.Short ?? a.Name) : member[(member.LastIndexOf('/') + 1)..];
        List<(string Text, string Path)> crumbs = [("ah", "0"), (TuiText.Plain(TeamSlices.NameOf(slices, team)), "1"), (name, "2")];
        var right = agent is { } r
            ? Join(StateWord(r.Status, r.Since), r.Live, r.Tool is { } tool ? "tool " + TuiText.Plain(tool) : string.Empty, TuiText.Plain(r.StateBranch ?? string.Empty))
            : string.Empty;
        var body = new List<TuiLine> { new("WORK ITEMS", TuiLineKind.Heading) };
        var ids = new List<string>();
        var lines = new List<int>();
        // A team with no streams has no plan slice: once the id has settled without arriving, there is nothing to wait for.
        var plan = TeamSlices.PlanIds(slices, team)[0];
        var items = !slices.ContainsKey(plan) && input.Answered?.Invoke([plan]) == true ? [] : TeamSlices.HeldItems(slices, team, member, input.Now);
        if (items is null)
        {
            body.Add(new TuiLine(Loading, TuiLineKind.Dim));
        }
        else if (items.Count == 0)
        {
            body.Add(new TuiLine(NoItems, TuiLineKind.Dim));
        }
        else
        {
            ids.AddRange(items.Select(static i => i.Id));
            var chosen = plain ? -1 : Chosen(s.Items, ids);
            const int State = 14;
            const int Age = 5;
            var total = cols - 2;
            var key = Math.Min(items.Max(static i => TuiText.Width(i.Key)), Math.Max(4, total / 3));
            var withAge = total - key - State - Age - 6 >= 8;
            var withState = withAge || total - key - State - 4 >= 8;
            var title = Math.Max(1, total - key - 2 - (withState ? State + 2 : 0) - (withAge ? Age + 2 : 0));
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var cells = new List<(string Text, int Width, bool Right)> { (item.Key, key, false) };
                if (withState)
                {
                    cells.Add((StateText(item.State), State, false));
                }

                cells.Add((item.Title, title, false));
                if (withAge)
                {
                    cells.Add((item.Age, Age, true));
                }

                lines.Add(body.Count);
                body.Add(Row(i == chosen ? "▸ " : "  ", cells, i == chosen));
            }
        }

        return new Page(crumbs, right, [], body, ids, lines, "click or ⏎ open  esc team  q quit");
    }

    /// <summary>A work item's popup over the frame on show, centred, four fifths of the screen each way: its state, holder, note and log, scrolled inside.</summary>
    private static TuiFrame WithDialog(TuiFrame under, string card, int scroll, TuiScreenInput input, int cols, int rows)
    {
        var w = Math.Min(cols, Math.Max(40, cols * 4 / 5));
        var h = Math.Min(rows, Math.Max(10, rows * 4 / 5));
        var (x0, y0) = ((cols - w) / 2, (rows - h) / 2);
        var inner = Math.Max(1, w - 4);
        var height = Math.Max(0, h - 2);
        var detail = TeamSlices.Detail(input.Slices, card);
        var answered = input.Answered?.Invoke(TeamSlices.DetailIds(input.Slices, card)) == true;
        var body = DetailBody(detail, answered, input, inner);
        var top = Math.Clamp(scroll, 0, Math.Max(0, body.Count - height));
        var title = TuiText.Fit(detail?.Title ?? "Work item", Math.Max(1, w - 8));
        var foot = TuiText.Fit("─ esc close ", Math.Max(1, w - 2));
        List<string> box =
        [
            "┌ " + TuiText.Pad(title, Math.Max(1, w - 8)) + " [×] ┐",
            .. Enumerable.Range(top, height).Select(i => "│ " + TuiText.Pad(TuiText.Fit(i < body.Count ? body[i].Text : string.Empty, inner), inner) + " │"),
            "└" + foot + new string('─', Math.Max(0, w - 2 - TuiText.Width(foot))) + "┘",
        ];
        var lines = under.Lines.ToList();
        for (var i = 0; i < box.Count && y0 + i < lines.Count; i++)
        {
            lines[y0 + i] = new TuiLine(Over(lines[y0 + i].Text, x0, box[i], cols));
        }

        if (input.Fault.Length == 0 && y0 + box.Count < lines.Count)
        {
            lines[^1] = new TuiLine(TuiText.Fit("esc close  ↑↓ scroll  q quit", cols), TuiLineKind.Dim);
        }

        return under with { Lines = lines, Hit = Hit.Empty, RowIds = [], RowLines = [], BodyLines = body.Count, BodyHeight = height, Box = new TuiBox(x0, y0, x0 + w, y0 + h, x0 + w - 5) };
    }

    /// <summary>The line with the box's line laid over it from <paramref name="x"/>.</summary>
    private static string Over(string under, int x, string box, int cols)
    {
        return TuiText.Fit(TuiText.Pad(TuiText.Take(under, x), x) + box + TuiText.Skip(under, x + TuiText.Width(box)), cols);
    }

    private static List<TuiLine> DetailBody(TeamSlices.ItemDetail? d, bool answered, TuiScreenInput input, int cols)
    {
        var body = new List<TuiLine>();
        if (d is null)
        {
            body.Add(new TuiLine(answered ? NoItem : Loading, TuiLineKind.Dim));
            return body;
        }

        if (d.Gone)
        {
            body.Add(new TuiLine(NoItem, TuiLineKind.Dim));
            return body;
        }

        body.AddRange(TuiText.Wrap(d.Title, cols).Select(static l => new TuiLine(l)));
        body.Add(new TuiLine(string.Empty));
        body.Add(new TuiLine("Item    " + d.Key));
        body.Add(new TuiLine("State   " + (d.State.Length > 0 ? StateText(d.State) : "─")));
        body.Add(new TuiLine("Holder  " + (d.Holder.Length > 0 ? d.Holder : "─")));
        if (d.Note.Length > 0)
        {
            body.Add(new TuiLine(string.Empty));
            body.Add(new TuiLine("NOTE", TuiLineKind.Heading));
            body.AddRange(TuiText.Wrap(d.Note, cols).Select(static l => new TuiLine(l)));
        }

        if (d.LogId is { } id)
        {
            body.Add(new TuiLine(string.Empty));
            AddLog(body, id, d.Key, input, cols);
        }

        return body;
    }

    /// <summary>An item's log, oldest first.</summary>
    private static void AddLog(List<TuiLine> body, string id, string key, TuiScreenInput input, int cols)
    {
        var log = input.Slices.GetValueOrDefault(id) is { IsMap: true } served ? TeamSlices.LogOf(served, input.Now, input.Zone) : null;
        if (log is { Description.Count: > 0 })
        {
            body.Add(new TuiLine(TuiText.Fit("DESCRIPTION", cols), TuiLineKind.Heading));
            body.AddRange(log.Description.SelectMany(l => TuiText.Wrap(l, Math.Max(1, cols))).Select(l => new TuiLine(l)));
            body.Add(new TuiLine(string.Empty));
        }

        var title = "LOG  " + key;
        var more = log is { More: > 0 } ? log.More.ToString(CultureInfo.InvariantCulture) + " older not shown" : string.Empty;
        var gap = cols - TuiText.Width(title) - TuiText.Width(more);
        body.Add(new TuiLine(more.Length > 0 && gap >= 2 ? title + new string(' ', gap) + more : TuiText.Fit(title, cols), TuiLineKind.Heading));
        if (log is null)
        {
            // A null body says there is no such item; a slice that never arrives is not served either way.
            var answered = input.Slices.ContainsKey(id) || input.Answered?.Invoke([id]) == true;
            body.Add(new TuiLine(answered ? NoLog : Loading, TuiLineKind.Dim));
            return;
        }

        const int When = 7;
        const int Type = 10;
        const int By = 14;
        var withBy = cols - When - Type - By - 6 >= 8;
        var withType = cols - When - Type - 4 >= 8;
        var text = Math.Max(1, cols - When - 2 - (withType ? Type + 2 : 0) - (withBy ? By + 2 : 0));
        foreach (var entry in log.Entries)
        {
            var cells = new List<(string Text, int Width, bool Right)> { (entry.When, When, false) };
            if (withType)
            {
                cells.Add((entry.Type, Type, false));
            }

            cells.Add((entry.Title, text, false));
            if (withBy)
            {
                cells.Add((entry.By, By, false));
            }

            body.Add(Row(string.Empty, cells, false));
        }

        if (log.Facts.Length > 0)
        {
            body.Add(new TuiLine(TuiText.Fit(log.Facts, cols), TuiLineKind.Dim));
        }
    }

    private static string StateText(string state)
    {
        var word = state.Replace('_', ' ');
        return state switch
        {
            "in_progress" => "▶ " + word,
            "done" => "✓ " + word,
            "blocked" => "‖ " + word,
            _ => "· " + word,
        };
    }

    /// <summary>One event on the state, given the frame on show: the next state and what the loop must do, if anything.</summary>
    internal static (TuiScreenState State, NavAction? Action) Reduce(TuiScreenState s, InputEvent ev, TuiFrame frame, IReadOnlyDictionary<string, JsonValue> slices)
    {
        if (ev is MouseEvent mouse)
        {
            return (Mouse(s, mouse, frame, slices), null);
        }

        var key = ((KeyEvent)ev).Name;
        if (s.Help)
        {
            return (s with { Help = false }, key is "q" or "ctrl-c" ? new QuitAction() : null);
        }

        if (s.Detail is not null)
        {
            return key switch
            {
                "q" or "ctrl-c" => (s, new QuitAction()),
                "r" => (s, new RefreshAction()),
                "esc" or "enter" => (s with { Detail = null }, null),
                "up" or "k" => (DetailScrolled(s, frame, -1), null),
                "down" or "j" => (DetailScrolled(s, frame, 1), null),
                "home" => (DetailScrolled(s, frame, -frame.BodyLines), null),
                "end" => (DetailScrolled(s, frame, frame.BodyLines), null),
                "pageup" => (DetailScrolled(s, frame, -Math.Max(1, frame.BodyHeight)), null),
                "pagedown" => (DetailScrolled(s, frame, Math.Max(1, frame.BodyHeight)), null),
                _ => (s, null),
            };
        }

        switch (key)
        {
            case "q":
            case "ctrl-c":
                return (s, new QuitAction());
            case "r":
                return (s, new RefreshAction());
            case "?":
                return (s with { Help = true }, null);
            case "up":
            case "k":
                return (Moved(s, frame, Selected(s, frame) - 1), null);
            case "down":
            case "j":
                return (Moved(s, frame, Selected(s, frame) + 1), null);
            case "home":
                return (Scrolled(Moved(s, frame, 0), frame, -frame.BodyLines), null);
            case "end":
                return (Scrolled(Moved(s, frame, frame.RowIds.Count - 1), frame, frame.BodyLines), null);
            case "pageup":
                return (Scrolled(s, frame, -Math.Max(1, frame.BodyHeight)), null);
            case "pagedown":
                return (Scrolled(s, frame, Math.Max(1, frame.BodyHeight)), null);
            case "enter":
                return (Selected(s, frame) is var at and >= 0 ? Open(s, frame, at) : s, null);
            case "esc":
                return (s.Depth > 0 ? ToDepth(s, s.Depth - 1, slices) : s, null);
            case "e" when s.Depth == 0:
                return (s with { Ended = !s.Ended }, null);
            default:
                return (s, null);
        }
    }

    private static TuiScreenState Mouse(TuiScreenState s, MouseEvent mouse, TuiFrame frame, IReadOnlyDictionary<string, JsonValue> slices)
    {
        if (s.Help)
        {
            return s;
        }

        if (s.Detail is not null)
        {
            return mouse.Kind switch
            {
                "wheelup" => DetailScrolled(s, frame, -WheelRows),
                "wheeldown" => DetailScrolled(s, frame, WheelRows),
                "click" when frame.Box is { } box && (mouse.X < box.X0 || mouse.X >= box.X1 || mouse.Y < box.Y0 || mouse.Y >= box.Y1 || (mouse.Y == box.Y0 && mouse.X >= box.CloseX && mouse.X < box.CloseX + 3)) => s with { Detail = null },
                _ => s,
            };
        }

        switch (mouse.Kind)
        {
            case "wheelup":
                return Scrolled(s, frame, -WheelRows);
            case "wheeldown":
                return Scrolled(s, frame, WheelRows);
            case "click":
                if (mouse.Y == 0)
                {
                    var crumb = frame.Hit.Crumbs.FirstOrDefault(c => mouse.X >= c.X0 && mouse.X < c.X1);
                    return crumb is not null && int.Parse(crumb.Path, CultureInfo.InvariantCulture) is var depth && depth < s.Depth ? ToDepth(s, depth, slices) : s;
                }

                return frame.Hit.Rows.FirstOrDefault(r => r.Y == mouse.Y) is { } row ? Open(s, frame, row.Index) : s;
            default:
                return s;
        }
    }

    private static TuiLevel LevelOf(TuiScreenState s) => s.Depth == 0 ? s.Teams : s.Depth == 1 ? s.Agents : s.Items;

    private static TuiScreenState WithLevel(TuiScreenState s, TuiLevel level) =>
        s.Depth == 0 ? s with { Teams = level } : s.Depth == 1 ? s with { Agents = level } : s with { Items = level };

    private static int Selected(TuiScreenState s, TuiFrame frame) => Chosen(LevelOf(s), frame.RowIds);

    /// <summary>The selection moved to a row, scrolled so that it shows.</summary>
    private static TuiScreenState Moved(TuiScreenState s, TuiFrame frame, int to)
    {
        if (frame.RowIds.Count == 0)
        {
            return s;
        }

        var at = Math.Clamp(to, 0, frame.RowIds.Count - 1);
        var level = LevelOf(s);
        var line = frame.RowLines[at];
        var scroll = Math.Clamp(level.Scroll, 0, Math.Max(0, frame.BodyLines - frame.BodyHeight));
        scroll = line < scroll ? line : line >= scroll + frame.BodyHeight ? line - frame.BodyHeight + 1 : scroll;
        return WithLevel(s, level with { Id = frame.RowIds[at], Index = at, Scroll = Math.Max(0, scroll) });
    }

    private static TuiScreenState Scrolled(TuiScreenState s, TuiFrame frame, int by)
    {
        var level = LevelOf(s);
        var most = Math.Max(0, frame.BodyLines - frame.BodyHeight);
        return WithLevel(s, level with { Scroll = Math.Clamp(Math.Clamp(level.Scroll, 0, most) + by, 0, most) });
    }

    private static TuiScreenState DetailScrolled(TuiScreenState s, TuiFrame frame, int by) =>
        s with { DetailScroll = Math.Clamp(s.DetailScroll + by, 0, Math.Max(0, frame.BodyLines - frame.BodyHeight)) };

    /// <summary>The move a click and <c>⏎</c> share: a team or an agent opens, a Sitrep heading folds, a work item opens its dialog.</summary>
    private static TuiScreenState Open(TuiScreenState s, TuiFrame frame, int at)
    {
        var id = frame.RowIds[at];
        var onTeam = s.Agents with { Id = id, Index = at };
        return s.Depth switch
        {
            0 => OpenTeam(s, id, at),
            1 when id.StartsWith(SitrepView.RegionStop, StringComparison.Ordinal) => s with { Agents = onTeam, SitrepToggled = Flipped(s.SitrepToggled, id[SitrepView.RegionStop.Length..]) },
            1 when id.StartsWith(SitrepView.ItemStop, StringComparison.Ordinal) => s with { Agents = onTeam, Detail = id[SitrepView.ItemStop.Length..], DetailScroll = 0 },
            1 => s with { Depth = 2, Agents = onTeam, Items = new() },
            _ => s with { Items = s.Items with { Id = id, Index = at }, Detail = $"{s.Teams.Id}|{id}", DetailScroll = 0 },
        };
    }

    private static HashSet<string> Flipped(IReadOnlySet<string> set, string name)
    {
        var flipped = new HashSet<string>(set, StringComparer.Ordinal);
        if (!flipped.Add(name))
        {
            _ = flipped.Remove(name);
        }

        return flipped;
    }

    /// <summary>Back to a level; leaving a team keeps what its Sitrep shows as the team's next baseline, and one that never loaded keeps the old one.</summary>
    private static TuiScreenState ToDepth(TuiScreenState s, int depth, IReadOnlyDictionary<string, JsonValue> slices)
    {
        if (depth > 0 || s.Teams.Id is not { } team || TeamSlices.SitrepSnap(slices, team) is not { } snap)
        {
            return s with { Depth = depth };
        }

        var seen = new Dictionary<string, IReadOnlyDictionary<string, int>>(s.Seen, StringComparer.Ordinal) { [team] = snap };
        return s with { Depth = depth, Seen = seen };
    }
}
