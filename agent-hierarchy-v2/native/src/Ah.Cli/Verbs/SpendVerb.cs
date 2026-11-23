using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah spend add|show</c> (ah2-512, adr-37): members count each paid call to an outside service; the totals are summed on read. Honour-system:
/// nothing here refuses or stops a call. <c>add</c> exits 0 whatever the totals are; only a usage error is exit 2.
/// </summary>
internal static partial class SpendVerb
{
    internal const string Usage =
        "usage: ah spend add <units> <unit> --what \"<text>\" [--item <work id>] [--json] [--cwd <dir>] | ah spend [show] [--run | --all | --item <work id>] [--json] [--cwd <dir>]";

    private static readonly HashSet<string> Bools = new(StringComparer.Ordinal) { "json", "run", "all" };

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal) { "what", "item", "cwd" };

    private static readonly string[] Selectors = ["run", "all", "item"];

    /// <summary>The caller as a row records it: its team (null with none), the name it counts under, its session, and the team's open run.</summary>
    private sealed record Counter(string? Team, string By, string? Session, string? Run);

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count > 0 && args[0] is "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return 0;
        }

        var add = args.Count > 0 && args[0] == "add";
        var first = add || (args.Count > 0 && args[0] == "show") ? 1 : 0;
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = first; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is not null && Bools.Contains(name))
            {
                flags[name] = null;
            }
            else if (name is not null && Values.Contains(name) && i + 1 < args.Count)
            {
                flags[name] = args[++i];
            }
            else if (name is null && add)
            {
                positional.Add(args[i]);
            }
            else
            {
                return Fail(stderr, $"ah spend: {args[i]} is not understood; {Usage}");
            }
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah spend: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        var cwd = flags.GetValueOrDefault("cwd") ?? Directory.GetCurrentDirectory();
        var dir = BacklogVerb.DirOf(g, cwd);
        return add ? Add(context, g, cwd, dir, positional, flags, stdout, stderr) : Show(context, g, dir, flags, stdout, stderr);
    }

    private static int Fail(TextWriter stderr, string message, int code = 2)
    {
        stderr.WriteLine(message);
        return code;
    }

    private static Counter CounterOf(CliContext context, ServiceGraph g, string dir)
    {
        var team = RulesStoreVerb.CallerAsk(context.Env, g, dir).Team;
        return new Counter(team, BacklogVerb.Caller(context.Env, g, dir), context.Env.GetVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } s ? s : null, RunOf(g, dir, team));
    }

    /// <summary>The team's open run anchor id, as <c>ah msg decision add</c> resolves it; null with no team, no open run or several.</summary>
    private static string? RunOf(ServiceGraph g, string dir, string? team)
    {
        if (team is null)
        {
            return null;
        }

        try
        {
            return g.Hierarchy.Decisions.OpenRunAnchor(dir, team).Get("id") is { IsText: true } id ? id.AsString() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="read"/> over a fresh reader; <paramref name="none"/> when the database cannot be read.</summary>
    private static T Read<T>(string ahHome, T none, Func<Func<string, object?[], IReadOnlyList<object?[]>>, T> read)
    {
        try
        {
            if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
            {
                return none;
            }

            using (reader)
            {
                return read(reader.Query);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return none;
        }
    }

    private static string? ItemId(string? prefix, long? n) => n is { } item ? HierarchyModule.WorkId(prefix ?? "w", item) : null;

    private static int Add(CliContext context, ServiceGraph g, string cwd, string dir, List<string> positional, Dictionary<string, string?> flags, TextWriter stdout, TextWriter stderr)
    {
        if (positional.Count != 2)
        {
            return Fail(stderr, $"ah spend add: give the count and the unit; {Usage}");
        }

        if (HierarchyModule.SpendMicrosOf(positional[0]) is not { } micros)
        {
            return Fail(stderr, $"ah spend add: {positional[0]} is not a non-zero number with at most 6 decimal places; {Usage}");
        }

        if (HierarchyModule.SpendUnitOf(positional[1]) is not { } unit)
        {
            return Fail(stderr, $"ah spend add: a unit is 1 to 40 of a-z, 0-9 and -; got {positional[1]}; {Usage}");
        }

        if (flags.GetValueOrDefault("what") is not { } what || what.Trim().Length == 0 || what.Length > HierarchyModule.SpendMaxWhat)
        {
            return Fail(stderr, $"ah spend add: --what names the call in 1 to {HierarchyModule.SpendMaxWhat} characters; {Usage}");
        }

        var ahHome = context.Env.AhHome;
        var counter = CounterOf(context, g, dir);
        long? item = null;
        if (flags.GetValueOrDefault("item") is { } named)
        {
            item = WorkVerb.Resolve(g, ahHome, dir, named, null).Row?.N;
            if (item is null)
            {
                stderr.WriteLine($"ah spend add: no work item {named}; counted with no item");
            }
        }
        else
        {
            try
            {
                item = WorkVerb.HeldItem(context.Env, g, cwd)?.N;
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                item = null;
            }
        }

        static JsonValue Or(string? text) => text is null ? JsonValue.Null : text;
        var args = new JsonObject
        {
            ["dir"] = dir,
            ["team"] = Or(counter.Team),
            ["run"] = Or(counter.Run),
            ["item"] = item is { } n ? n : JsonValue.Null,
            ["unit"] = unit,
            ["micros"] = micros,
            ["what"] = what,
            ["by"] = counter.By,
            ["session"] = Or(counter.Session),
            ["ts_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        try
        {
            _ = g.Hierarchy.StateCalls.Spend("add", args);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return Fail(stderr, $"ah spend add: the count was not recorded: {e.Message}", 1);
        }

        var (prefix, rows) = Read<(string?, IReadOnlyList<SpendRow>)>(ahHome, (null, []), q => (HierarchyModule.WorkPrefix(q, dir), HierarchyModule.SpendRows(q, dir)));
        var phaseOf = PhasesOf(g, ahHome, dir);
        var caps = Caps(ahHome, dir, prefix, rows, team => team == counter.Team ? counter.Run : null, counter.Run, phaseOf)
            .Where(c => c.Unit == unit && Counts(c, counter.Team, item, phaseOf)).ToList();
        var teamCap = caps.FirstOrDefault(c => c.Row.Level == RulesOfEngagement.Team && !c.Row.Off);
        var total = teamCap?.Total ?? rows.Where(r => r.Unit == unit && r.Team == counter.Team && r.Run == counter.Run).Sum(r => r.Micros);
        var itemId = ItemId(prefix, item);
        var reached = new List<(CapSum Cap, string? Id, bool New, string? Error)>();
        foreach (var cap in caps.Where(c => !c.Row.Off && c.Total >= c.Max))
        {
            var (id, fresh, error) = Park(g, dir, counter, cap);
            if (error is not null)
            {
                stderr.WriteLine($"ah spend add: warning: the cap for {cap.Scope} is reached but the user could not be asked: {error}; the count is kept");
            }

            reached.Add((cap, id, fresh, error));
        }

        if (flags.ContainsKey("json"))
        {
            var capArray = new JsonArray();
            foreach (var cap in caps)
            {
                var park = reached.FirstOrDefault(r => r.Cap == cap);
                capArray.Add(new JsonObject
                {
                    ["scope"] = cap.Scope,
                    ["total"] = HierarchyModule.SpendFormat(cap.Total),
                    ["max"] = HierarchyModule.SpendFormat(cap.Max),
                    ["state"] = cap.Row.Off ? "off" : park.Id is { } q ? $"asked {q}" : cap.Total >= cap.Max ? "reached" : "under",
                    ["question"] = Or(park.Id),
                });
            }

            stdout.WriteLine(JsonText.Stringify(new JsonObject
            {
                ["counted"] = HierarchyModule.SpendFormat(micros),
                ["unit"] = unit,
                ["item"] = Or(itemId),
                ["team"] = Or(counter.Team),
                ["run"] = Or(counter.Run),
                ["total"] = HierarchyModule.SpendFormat(total),
                ["caps"] = capArray,
            }, 0));
            return 0;
        }

        var on = itemId is null ? string.Empty : $" for {itemId}";
        var of = teamCap is null ? string.Empty : "/" + HierarchyModule.SpendFormat(teamCap.Max);
        var period = counter.Run is not null ? "run total" : teamCap is null ? "total" : "total since the cap was set";
        var line = new System.Text.StringBuilder($"counted {HierarchyModule.SpendFormat(micros)} {unit}{on} ({period} {HierarchyModule.SpendFormat(total)}{of} {unit}).");
        foreach (var (cap, id, fresh, _) in reached)
        {
            var asked = id is null ? "the user could not be asked" : fresh ? $"asked the user ({id})" : $"already asked the user ({id})";
            _ = line.Append($" Cap reached for {cap.Scope} ({HierarchyModule.SpendFormat(cap.Total)}/{HierarchyModule.SpendFormat(cap.Max)} {unit}): {asked}; calls are not stopped.");
        }

        stdout.WriteLine(line.ToString());
        return 0;
    }

    private static int Show(CliContext context, ServiceGraph g, string dir, Dictionary<string, string?> flags, TextWriter stdout, TextWriter stderr)
    {
        if (Selectors.Count(flags.ContainsKey) > 1)
        {
            return Fail(stderr, $"ah spend: at most one of --run, --all and --item; {Usage}");
        }

        if (flags.ContainsKey("what"))
        {
            return Fail(stderr, $"ah spend: --what goes with add; {Usage}");
        }

        var ahHome = context.Env.AhHome;
        var (prefix, rows) = Read<(string?, IReadOnlyList<SpendRow>)>(ahHome, (null, []), q => (HierarchyModule.WorkPrefix(q, dir), HierarchyModule.SpendRows(q, dir)));
        string title;
        Counter? counter = null;
        IEnumerable<SpendRow> shown;
        var all = flags.ContainsKey("all");
        if (all)
        {
            title = "spend, all runs";
            shown = rows;
        }
        else if (flags.GetValueOrDefault("item") is { } named)
        {
            if (WorkVerb.Resolve(g, ahHome, dir, named, null).Row is not { } row)
            {
                return Fail(stderr, $"ah spend: no work item {named}", 1);
            }

            title = $"spend on {ItemId(prefix, row.N)}";
            shown = rows.Where(r => r.Item == row.N);
        }
        else
        {
            counter = CounterOf(context, g, dir);
            title = counter.Run is { } run ? $"spend, run {run}" : "spend, no open run";
            if (counter.Team is { } team)
            {
                title += $" (team {team})";
            }

            shown = rows.Where(r => r.Team == counter.Team && r.Run == counter.Run);
        }

        var list = shown.ToList();
        var phaseOf = PhasesOf(g, ahHome, dir);
        var you = counter ?? CounterOf(context, g, dir);
        var caps = Caps(ahHome, dir, prefix, rows, team => team == you.Team ? you.Run : RunOf(g, dir, team), you.Run, phaseOf)
            .Where(c => all || (counter is not null && c.Row.Level == RulesOfEngagement.Team && c.Row.Scope == counter.Team) || list.Any(r => Counts(c, r.Team, r.Item, phaseOf)))
            .Select(c => (Cap: c, State: StateOf(g, dir, c, you.Team))).ToList();
        var units =list.GroupBy(r => r.Unit, StringComparer.Ordinal).OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => (Unit: u.Key, Total: u.Sum(r => r.Micros))).ToList();
        var items = list.GroupBy(r => (r.Item, r.Unit)).OrderBy(x => x.Key.Item ?? long.MaxValue).ThenBy(x => x.Key.Unit, StringComparer.Ordinal)
            .Select(x => (Item: ItemId(prefix, x.Key.Item), x.Key.Unit, Total: x.Sum(r => r.Micros))).ToList();
        var members = list.GroupBy(r => (r.By, r.Unit)).OrderBy(x => x.Key.By, StringComparer.Ordinal).ThenBy(x => x.Key.Unit, StringComparer.Ordinal)
            .Select(x => (x.Key.By, x.Key.Unit, Total: x.Sum(r => r.Micros))).ToList();
        if (flags.ContainsKey("json"))
        {
            var unitArray = new JsonArray();
            foreach (var (unit, total) in units)
            {
                unitArray.Add(new JsonObject { ["unit"] = unit, ["total"] = HierarchyModule.SpendFormat(total) });
            }

            var itemArray = new JsonArray();
            foreach (var (item, unit, total) in items)
            {
                itemArray.Add(new JsonObject { ["item"] = item is null ? JsonValue.Null : item, ["unit"] = unit, ["total"] = HierarchyModule.SpendFormat(total) });
            }

            var memberArray = new JsonArray();
            foreach (var (by, unit, total) in members)
            {
                memberArray.Add(new JsonObject { ["by"] = by, ["unit"] = unit, ["total"] = HierarchyModule.SpendFormat(total) });
            }

            var capArray = new JsonArray();
            foreach (var (cap, state) in caps)
            {
                capArray.Add(new JsonObject
                {
                    ["scope"] = cap.Scope, ["unit"] = cap.Unit, ["total"] = HierarchyModule.SpendFormat(cap.Total), ["max"] = HierarchyModule.SpendFormat(cap.Max), ["period"] = cap.Period, ["state"] = state,
                });
            }

            stdout.WriteLine(JsonText.Stringify(new JsonObject
            {
                ["team"] = counter?.Team is { } t ? t : JsonValue.Null,
                ["run"] = counter?.Run is { } r ? r : JsonValue.Null,
                ["units"] = unitArray,
                ["items"] = itemArray,
                ["members"] = memberArray,
                ["caps"] = capArray,
            }, 0));
            return 0;
        }

        stdout.WriteLine(title + ":");
        if (units.Count == 0)
        {
            stdout.WriteLine("  nothing counted");
        }

        foreach (var (unit, total) in units)
        {
            stdout.WriteLine($"  {HierarchyModule.SpendFormat(total)} {unit}");
        }

        if (caps.Count > 0)
        {
            stdout.WriteLine("caps:");
            foreach (var (cap, state) in caps)
            {
                stdout.WriteLine($"  {cap.Scope}: {HierarchyModule.SpendFormat(cap.Total)}/{HierarchyModule.SpendFormat(cap.Max)} {cap.Unit} ({cap.Period}), {state}");
            }
        }

        if (units.Count == 0)
        {
            return 0;
        }

        stdout.WriteLine("by item:");
        foreach (var (item, unit, total) in items)
        {
            stdout.WriteLine($"  {item ?? "no item"}: {HierarchyModule.SpendFormat(total)} {unit}");
        }

        stdout.WriteLine("by member:");
        foreach (var (by, unit, total) in members)
        {
            stdout.WriteLine($"  {by}: {HierarchyModule.SpendFormat(total)} {unit}");
        }

        return 0;
    }
}
