using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.StateDb;
using Ah.Watch;

namespace Ah.Cli.Verbs;

/// <summary>What <c>ah backlog</c> needs: the writer and its context, the state database, the project's hierarchy directory, the origin remote and who is writing.</summary>
internal sealed record BacklogDeps(IStateWriter Writer, RequestContext Context, string AhHome, string Dir, string? OriginUrl, string By, Func<string, string?>? WorkAlias = null, Func<IReadOnlyList<PlanItem>>? DirItems = null, Func<string, IReadOnlyList<string>>? CloseChildren = null);

/// <summary>
/// <c>ah backlog list|show|next|add|set|claim|done|drop|import|export|groomed</c>: the project's backlog, the one plan stream kept in the
/// state database. Every write is one <c>progress.backlog-write</c> op; every read is an indexed query of <c>plan_items</c>.
/// </summary>
internal static class BacklogVerb
{
    private const string Usage =
        "usage: ah backlog list [--wave w] [--state s] [--theme t] [--holder h] [--kind k] [--json] | show <id> [--history] [--json] | next [--theme t] [--json]"
        + " | add <title> [--outcome o] [--theme t] [--size S|M|L] [--wave w] [--after id] [--depends ids] [--source s]... [--ext ref] [--kind project|goal|epic|deliverable|task] [--parent id|none]"
        + " | set <id> [--title t] [--outcome o] [--theme t] [--size s] [--wave w] [--after id] [--depends ids] [--source s]... [--ext ref] [--note n] [--kind k] [--parent id|none] [--state dropped|blocked --note why]"
        + " | claim <id> --holder h | done <id> [--commit sha]... [--note n] | drop <id> --note why | testing <sha> <id>... | testing --clear <id>... | import <file.json> | export [--since ms|ISO] [--json]"
        + " | groomed --summary json | health [--json]   (all take [--cwd dir] [--by name])";

    private static readonly HashSet<string> Bools = new(StringComparer.Ordinal) { "json", "history", "clear" };

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "wave", "state", "theme", "holder", "outcome", "size", "after", "depends", "source", "ext", "title", "note", "commit", "since", "summary", "cwd", "by", "kind", "parent",
    };

    /// <summary>True when <paramref name="text"/> has the shape of a backlog id: <c>b</c> and a number.</summary>
    internal static bool IsBacklogId(string text) => HierarchyModule.IsBacklogId(text);

    internal static string DirOf(ServiceGraph g, string cwd) => g.Config.MainHierarchyDir(cwd) ?? g.Config.HierarchyDir(cwd);

    /// <summary>True when the backlog of <paramref name="cwd"/>'s project holds item <paramref name="id"/>.</summary>
    internal static bool Has(ServiceGraph g, string ahHome, string cwd, string id)
    {
        if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
        {
            return false;
        }

        using (reader)
        {
            return HierarchyModule.BacklogGet(reader.Query, DirOf(g, cwd), id) is not null;
        }
    }

    /// <summary>Records a ref event linking exchange <paramref name="reference"/> on backlog item <paramref name="id"/>; the failure, or null.</summary>
    internal static string? Link(ServiceGraph g, string ahHome, string cwd, string id, string reference, bool remove = false)
    {
        var dir = DirOf(g, cwd);
        var args = new JsonObject { ["dir"] = dir, ["by"] = "orchestrator", ["item"] = id, [remove ? "unref" : "ref"] = reference };
        var result = g.Writer.Invoke("progress", HierarchyModule.BacklogWriteOp, JsonText.ToUtf8(args, 0), g.Hierarchy.Messages.PoolContext(dir), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
        return result.Ok ? null : result.Error ?? result.ErrorCode ?? "the write failed";
    }

    /// <summary>A ship closed the last open item under backlog row <paramref name="id"/>: an open row closes with <paramref name="sha"/>; a closed or unknown one is left alone. The failure, or null.</summary>
    internal static string? CloseShipped(ServiceGraph g, string ahHome, string dir, string id, string sha) =>
        CloseDone(g, ahHome, dir, id, [sha], $"shipped {(sha.Length > 7 ? sha[..7] : sha)}");

    /// <summary>
    /// Closes open backlog row <paramref name="id"/> with <paramref name="commits"/> and <paramref name="note"/>; a closed or unknown row, a project or a goal is left alone,
    /// and with <paramref name="subItemsHold"/> (board-truth Amendment 2) so is a row with an open backlog sub-item. The failure, or null.
    /// </summary>
    internal static string? CloseDone(ServiceGraph g, string ahHome, string dir, string id, IReadOnlyList<string> commits, string note, bool subItemsHold = false)
    {
        if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
        {
            return "the state database cannot be read";
        }

        using (reader)
        {
            // a project or goal is closed by hand only
            if (HierarchyModule.BacklogGet(reader.Query, dir, id) is not { } row || row.Item.State is "done" or "dropped" || HierarchyModule.WorkIsContainerKind(row.Item.Kind)
                || (subItemsHold && HierarchyModule.BacklogList(reader.Query, dir).Any(r => r.Item.Parent == id && r.Item.State is not ("done" or "dropped"))))
            {
                return null;
            }
        }

        var args = new JsonObject { ["dir"] = dir, ["by"] = "orchestrator", ["item"] = id, ["done"] = true, ["done_note"] = note };
        if (commits.Count > 0)
        {
            var list = new JsonArray();
            foreach (var commit in commits)
            {
                list.Add(commit);
            }

            args["commits"] = list;
        }

        var result = g.Writer.Invoke("progress", HierarchyModule.BacklogWriteOp, JsonText.ToUtf8(args, 0), g.Hierarchy.Messages.PoolContext(dir), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
        return result.Ok ? null : result.Error ?? result.ErrorCode ?? "the write failed";
    }

    /// <summary>
    /// Who is writing: the member (<c>AH_MEMBER_NAME</c>), else the role the hooks recorded for the calling session (<c>CLAUDE_CODE_SESSION_ID</c>),
    /// else <c>orchestrator</c> when that session is the team's Orchestrator, else <c>user</c>.
    /// </summary>
    internal static string Caller(IAhEnvironment env, ServiceGraph g, string dir, string? payloadSession = null)
    {
        if (env.GetVariable("AH_MEMBER_NAME") is { Length: > 0 } member)
        {
            return member;
        }

        if ((payloadSession ?? env.GetVariable("CLAUDE_CODE_SESSION_ID")) is not { Length: > 0 } session)
        {
            return "user";
        }

        if (g.Config.ReadSessionRole(JsonValue.FromString(session)) is { IsText: true } role && role.AsString().Length > 0)
        {
            return role.AsString();
        }

        try
        {
            return g.Roster.ReadTeamRecord(dir).Member("orchestrator")?.Member("session_id") is { IsText: true } o && o.AsString() == session ? "orchestrator" : "user";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return "user";
        }
    }

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah backlog: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        var cwd = Flag(args, "cwd") ?? Directory.GetCurrentDirectory();
        var dir = DirOf(g, cwd);
        var origin = OriginOf(dir);

        var by = Flag(args, "by") ?? Caller(context.Env, g, dir);
        string? Alias(string id) => WorkVerb.Resolve(g, context.Env.AhHome, dir, id, WorkVerb.CallerTeam(context.Env)).Row is { IsBacklog: true } row ? row.Item : null;
        // the checklist items of every team in the directory whose parent is the backlog item just closed
        // the held children left open share one line: "<id> is done; left open under it: <key> (<state>)[, …]"
        IReadOnlyList<string> CloseChildren(string parent)
        {
            var results = new string?[] { null }.Concat(g.Roster.ListTeamNames(dir)).Select(HierarchyModule.ProgressTeamOf).Distinct(StringComparer.Ordinal)
                .Select(folder => HierarchyModule.ChecklistCloseChildren(g.Hierarchy.Progress, dir, folder, parent, DateTimeOffset.UtcNow, HierarchyModule.OrchestratorAddress(folder), null)).ToList();
            var left = results.SelectMany(static r => r.Skipped).ToList();
            return [.. results.SelectMany(static r => r.Items.Select(static i => "closed checklist item " + i)), .. left.Count > 0 ? [$"{parent} is done; left open under it: {string.Join(", ", left)}"] : Array.Empty<string>()];
        }
        return Run(new BacklogDeps(g.Writer, g.Hierarchy.Messages.PoolContext(dir), context.Env.AhHome, dir, origin, by, Alias, () => Composition.SitrepRows.DirItems(g, dir), CloseChildren), args, stdout, stderr);
    }

    /// <summary>The origin remote's URL of the repository <paramref name="dir"/> is in; null when there is none.</summary>
    internal static string? OriginOf(string dir) =>
        AhPaths.RepoRootOf(dir) is { } root && File.Exists(Path.Join(root, ".git", "config"))
            ? WatchModule.OriginUrl(File.ReadAllText(Path.Join(root, ".git", "config")))
            : null;

    /// <summary>
    /// What the web view's <c>backlog:&lt;dirId&gt;</c> slice of the hierarchy <paramref name="dir"/> is made from: the items in list order and the health,
    /// read like <see cref="PlanFor"/> and <see cref="HealthFor"/>; no items and null health when there is no database or it cannot be read.
    /// </summary>
    internal static Ah.Watch.Model.BacklogFacts FactsFor(string ahHome, Ah.Config.IConfigCore config, string dir, long nowMs, Func<string, IReadOnlyList<PlanItem>>? dirItems = null)
    {
        var items = PlanFor(ahHome, config, dir)?.Items ?? [];
        return new Ah.Watch.Model.BacklogFacts(items, HealthFor(ahHome, config, dir, nowMs, evenEmpty: true, dirItems), OriginOf(dir));
    }

    /// <summary>
    /// The backlog health of the project hierarchy dir <paramref name="dir"/> belongs to (a worktree's resolves to its main checkout's),
    /// with the backlog-watch stamp from the tick stamps. Null when there is no database or it cannot be read, and, unless
    /// <paramref name="evenEmpty"/>, when the backlog has no items and was never groomed. <paramref name="dirItems"/> gives the checklist items of a hierarchy dir.
    /// </summary>
    internal static BacklogHealth? HealthFor(string ahHome, Ah.Config.IConfigCore config, string dir, long nowMs, bool evenEmpty, Func<string, IReadOnlyList<PlanItem>>? dirItems = null)
    {
        var key = config.MainHierarchyDir(AhPaths.RepoRootOf(dir) ?? dir) ?? dir;
        try
        {
            if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
            {
                return null;
            }

            using (reader)
            {
                var health = HierarchyModule.BacklogHealthOf(reader.Query, key, nowMs, StampMs(ahHome), dirItems?.Invoke(key));
                return evenEmpty || HierarchyModule.BacklogShown(health) ? health : null;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The backlog health facts of <paramref name="dir"/>'s project backlog (the key <see cref="HealthFor"/> reads), or null with no database.</summary>
    internal static Ah.Core.Json.JsonValue? FactsFor(string ahHome, Ah.Config.IConfigCore config, string dir, Func<string, IReadOnlyList<PlanItem>>? dirItems = null)
    {
        var key = config.MainHierarchyDir(AhPaths.RepoRootOf(dir) ?? dir) ?? dir;
        if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
        {
            return null;
        }

        using (reader)
        {
            return HierarchyModule.BacklogHealthFacts(reader.Query, key, dirItems?.Invoke(key));
        }
    }

    /// <summary>The backlog of the project <paramref name="dir"/> belongs to, in list order, as a plan; null when there is no database or no item.</summary>
    internal static PlanState? PlanFor(string ahHome, Ah.Config.IConfigCore config, string dir)
    {
        var key = config.MainHierarchyDir(AhPaths.RepoRootOf(dir) ?? dir) ?? dir;
        try
        {
            if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
            {
                return null;
            }

            using (reader)
            {
                var items = HierarchyModule.BacklogList(reader.Query, key);
                return items.Count == 0 ? null : new PlanState([.. items.Select(static r => r.Item)], 0);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>When the backlog-watch job last ran, from the tick stamps in the state database; null when it never did or they cannot be read.</summary>
    private static long? StampMs(string ahHome)
    {
        try
        {
            var ticks = JsonText.Parse(Ah.Cli.Composition.StallStore.Read(ahHome, Ah.Cli.Composition.StallStore.Ticks) ?? throw new IOException("no tick stamps"));
            return ticks.Member(HierarchyModule.BacklogWatchJob)?.Member("last_run_at") is { IsText: true } at
                && DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUnixTimeMilliseconds()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private static string? Flag(string[] args, string key)
    {
        var at = Array.LastIndexOf(args, "--" + key);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    internal static int Run(BacklogDeps d, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var words = new List<string>();
        var flags = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(args[i]);
                continue;
            }

            var key = args[i][2..];
            if (Bools.Contains(key))
            {
                flags.Add(new(key, "true"));
            }
            else if (Values.Contains(key) && i + 1 < args.Length)
            {
                flags.Add(new(key, args[++i]));
            }
            else
            {
                stderr.WriteLine(Values.Contains(key) ? $"ah backlog: --{key} needs a value" : $"ah backlog: unknown flag --{key}; {Usage}");
                return 2;
            }
        }

        if (words.Count == 0)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        var cmd = new Command(d, words, flags, stdout, stderr);
        try
        {
            return cmd.Dispatch();
        }
        catch (BacklogFailure failure)
        {
            (failure.Code == 3 ? stdout : stderr).WriteLine(failure.Code == 3 ? failure.Message : "ah backlog: " + failure.Message);
            return failure.Code;
        }
    }

    /// <summary>The write args of an external id: the id, and the issue number when it names an issue of the origin repository.</summary>
    internal static void SetExt(JsonObject args, string ext, string? originUrl)
    {
        args["ext"] = ext;
        if (WatchModule.OriginRepo(originUrl) is { } repo && ext.StartsWith($"github:{repo}#", StringComparison.Ordinal)
            && int.TryParse(ext[(repo.Length + 8)..], NumberStyles.None, CultureInfo.InvariantCulture, out var issue) && issue > 0)
        {
            args["ext_issue"] = issue;
        }
    }

    private sealed class BacklogFailure(string message, int code = 2) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed class Command(BacklogDeps d, List<string> words, List<KeyValuePair<string, string>> flags, TextWriter stdout, TextWriter stderr)
    {
        private string? F(string key) => flags.LastOrDefault(f => f.Key == key) is { Key: not null } hit ? hit.Value : null;

        private IEnumerable<string> All(string key) => flags.Where(f => f.Key == key).Select(static f => f.Value);

        private bool Json => F("json") is not null;

        private string Arg(int at, string what) => words.Count > at ? words[at] : throw new BacklogFailure($"{words[0]} needs {what}; {Usage}");

        /// <summary>The item the verb acts on: a work id that names a backlog row reads as that row; a <c>b&lt;N&gt;</c> is never looked up.</summary>
        private string ItemArg()
        {
            return Alias(Arg(1, "an item id"));
        }

        /// <summary>The backlog row a work id names; anything else stays as it was typed, for the write to judge.</summary>
        private string Alias(string id) => IsBacklogId(id) ? id : d.WorkAlias?.Invoke(id) ?? id;

        internal int Dispatch() => words[0] switch
        {
            "list" => List(),
            "show" => Show(),
            "next" => Next(),
            "add" => Add(Fields(new JsonObject { ["add"] = Arg(1, "a title") })),
            "set" => Write(SetState(Fields(new JsonObject { ["item"] = ItemArg() }))),
            "claim" => Write(new JsonObject { ["item"] = ItemArg(), ["claim"] = F("holder") ?? throw new BacklogFailure("claim needs --holder") }),
            "done" => DoneAndChildren(),
            "drop" => Write(new JsonObject { ["item"] = ItemArg(), ["drop"] = F("note") ?? throw new BacklogFailure("drop needs --note <why>") }),
            "testing" => Testing(),
            "import" => Import(),
            "export" => Export(),
            "groomed" => Groomed(),
            "health" => Health(),
            _ => throw new BacklogFailure(Usage),
        };

        /// <summary>The groom health, computed here from the database (so a stopped daemon shows as a stale job stamp), and the loop's three records.</summary>
        private int Health()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var health = Read(q => HierarchyModule.BacklogHealthOf(q, d.Dir, now, StampMs(d.AhHome), d.DirItems?.Invoke()), null)
                ?? throw new BacklogFailure("the state database cannot be read");
            var json = HierarchyModule.BacklogHealthJson(health, full: true);
            if (Json)
            {
                stdout.Write(JsonText.Stringify(json, 0) + "\n");
                return 0;
            }

            string T(string key) => json.Get(key) is { IsText: true } t ? t.AsString() : "never";
            stdout.Write(health.Due ? $"backlog: groom due since {T("due_since")} ({health.Reason})\n" : "backlog: ok\n");
            stdout.Write($"untriaged: {health.Untriaged}  stale: {health.Stale}\n");
            stdout.Write($"last groom: {T("last_groom_at")}{(health.LastGroomSummary is { } s ? " " + JsonText.Stringify(s, 0) : string.Empty)}\n");
            stdout.Write($"job {HierarchyModule.BacklogWatchJob}: last run {T("job_last_run_at")}\n");
            stdout.Write(health.Stalled ? "loop: STALLED\n" : "loop: ok\n");
            return 0;
        }

        // ------------------------------------------------------------ reads

        private T Read<T>(Func<Func<string, object?[], IReadOnlyList<object?[]>>, T> body, T empty)
        {
            // fail open: no database is an empty backlog
            if (StateDbModule.OpenReader(d.AhHome).Reader is not { } reader)
            {
                return empty;
            }

            using (reader)
            {
                return body(reader.Query);
            }
        }

        private JsonObject ItemJson(PlanItem item) => WatchModule.BacklogItemJson(item, d.OriginUrl);

        private static string Line(PlanItem i) => $"{i.Item} {i.State} {i.Wave ?? "untriaged"} {i.Ext ?? "-"} {i.Title}";

        private int List()
        {
            var state = F("state");
            var wave = F("wave");
            var rows = Read(q => HierarchyModule.BacklogList(q, d.Dir, wave, null, F("theme"), F("holder")), []);
            var kind = F("kind")?.ToLowerInvariant();
            Print(AsRead(rows.Select(static r => r.Item)).Where(i => (kind is null || (i.Kind ?? "task") == kind) && (state is null || i.State == state)));
            return 0;
        }

        /// <summary>The rows with the state each reads as, from the checklist items of the directory.</summary>
        private IReadOnlyList<PlanItem> AsRead(IEnumerable<PlanItem> rows) =>
            d.DirItems is { } items ? HierarchyModule.BacklogStates(rows, items()) : [.. rows];

        /// <summary><c>testing &lt;sha&gt; &lt;id&gt;...</c> marks rows as in the ship batch at that sha under QA; <c>--clear</c> takes the mark off when the batch fails. A ship closes a marked row like any other.</summary>
        private int Testing()
        {
            var clear = F("clear") is not null;
            var ids = words.Skip(clear ? 1 : 2).ToList();
            if (ids.Count == 0)
            {
                throw new BacklogFailure($"testing needs {(clear ? string.Empty : "a sha and ")}at least one id; {Usage}");
            }

            foreach (var id in ids)
            {
                var args = new JsonObject { ["item"] = Alias(id) };
                if (clear)
                {
                    args["testing_clear"] = true;
                }
                else
                {
                    args["testing"] = words[1];
                }

                if (Write(args) is var code and not 0)
                {
                    return code;
                }
            }

            return 0;
        }

        private void Print(IEnumerable<PlanItem> items)
        {
            if (Json)
            {
                var list = new JsonArray();
                foreach (var item in items)
                {
                    list.Add(ItemJson(item));
                }

                stdout.Write(JsonText.Stringify(list, 0) + "\n");
                return;
            }

            foreach (var item in items)
            {
                stdout.Write(Line(item) + "\n");
            }
        }

        private int Show()
        {
            var id = ItemArg();
            var (row, history, children) = Read(
                q => (HierarchyModule.BacklogGet(q, d.Dir, id), F("history") is null ? null : HierarchyModule.BacklogHistory(q, d.Dir, id),
                    HierarchyModule.BacklogList(q, d.Dir).Where(r => r.Item.Parent == id).Select(static r => r.Item.Item).ToList()),
                (null, null, []));
            if (row is null)
            {
                throw new BacklogFailure($"no backlog item {id}");
            }

            var item = AsRead([row.Item])[0];
            var shown = ItemJson(item);
            if (children.Count > 0)
            {
                shown["children"] = Strs(children);
            }

            var json = ItemJson(item);
            if (children.Count > 0)
            {
                json["children"] = Strs(children);
            }

            json["updated_ms"] = row.UpdatedMs;
            if (history is not null)
            {
                var list = new JsonArray();
                foreach (var e in history)
                {
                    list.Add(e);
                }

                json["history"] = list;
            }

            if (Json)
            {
                stdout.Write(JsonText.Stringify(json, 0) + "\n");
                return 0;
            }

            stdout.Write(JsonText.Stringify(shown, 2) + "\n");
            foreach (var e in history ?? [])
            {
                stdout.Write($"{ProgressText(e, "ts")} {ProgressText(e, "event")} by {ProgressText(e, "by")}  {JsonText.Stringify(e, 0)}\n");
            }

            return 0;
        }

        private static string ProgressText(JsonValue row, string key) => row.Member(key) is { IsText: true } t ? t.AsString() : "-";

        private int Next()
        {
            var theme = F("theme");
            if (Read(q => HierarchyModule.BacklogNext(q, d.Dir, theme, d.DirItems?.Invoke()), null) is not { } row)
            {
                return 1;
            }

            stdout.Write(Json ? JsonText.Stringify(ItemJson(row.Item), 0) + "\n" : Line(row.Item) + "\n");
            return 0;
        }

        private int Export()
        {
            long? since = null;
            if (F("since") is { } text)
            {
                since = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms
                    : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds()
                    : throw new BacklogFailure($"--since {text} is neither milliseconds nor an ISO time");
            }

            var rows = Read(q => HierarchyModule.BacklogExport(q, d.Dir, since), []);
            var asOf = rows.Count == 0 ? 0L : rows.Max(static r => r.UpdatedMs);
            if (!Json)
            {
                Print(rows.Select(static r => r.Item));
                return 0;
            }

            var items = new JsonArray();
            foreach (var r in rows)
            {
                var json = ItemJson(r.Item);
                json["updated_ms"] = r.UpdatedMs;
                items.Add(json);
            }

            // as_of_ms is the newest change the export saw, so passing it back as --since returns only later changes
            stdout.Write(JsonText.Stringify(new JsonObject { ["as_of_ms"] = Math.Max(asOf, since ?? 0), ["items"] = items }, 0) + "\n");
            return 0;
        }

        // ------------------------------------------------------------ writes

        private JsonObject Fields(JsonObject args)
        {
            void Copy(string flag, string key)
            {
                if (F(flag) is { } value)
                {
                    args[key] = value;
                }
            }

            Copy("title", "title");
            Copy("outcome", "outcome");
            Copy("theme", "theme");
            if (F("size") is { } size)
            {
                args["size"] = size.ToUpperInvariant();
            }

            if (F("wave") is { } wave)
            {
                args["wave"] = wave.ToLowerInvariant();
            }

            if (F("kind") is { } kind)
            {
                args["kind"] = kind.ToLowerInvariant();
            }

            if (F("parent") is { } parent)
            {
                args["parent"] = parent == "none" ? string.Empty : Alias(parent);
            }

            Copy("after", "after");
            Copy("note", "note");
            if (F("depends") is { } depends)
            {
                args["depends"] = Strs(depends.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(Alias));
            }

            if (All("source").ToList() is { Count: > 0 } sources)
            {
                args["sources"] = Strs(sources);
            }

            if (F("ext") is { } ext)
            {
                SetExt(args, ext, d.OriginUrl);
            }

            return args;
        }

        /// <summary><c>set --state</c>: dropped is the drop write and blocked the block write, each with <c>--note</c> as the reason; any other state names the verbs that set it.</summary>
        private JsonObject SetState(JsonObject args)
        {
            if (F("state") is not { } state)
            {
                return args;
            }

            var key = state switch
            {
                "dropped" => "drop",
                "blocked" => "block",
                _ => throw new BacklogFailure($"set --state takes dropped or blocked; other states have their own verbs: claim <id> --holder h | done <id> | drop <id> --note why | testing <sha> <id>; {Usage}"),
            };
            args[key] = F("note") ?? throw new BacklogFailure($"set --state {state} needs --note <why>");
            args.Remove("note");
            return args;
        }

        /// <summary><c>done</c>, then the checklist items whose parent is the closed item are closed too; each one closed or left open (held) is named on stderr.</summary>
        private int DoneAndChildren()
        {
            var args = Done();
            var code = Write(args);
            if (code == 0 && d.CloseChildren is { } close && args.Get("item") is { IsText: true } item && IsBacklogId(item.AsString()))
            {
                foreach (var child in close(item.AsString()))
                {
                    stderr.Write($"backlog done: {child}\n");
                }
            }

            return code;
        }

        private JsonObject Done()
        {
            var args = new JsonObject { ["item"] = ItemArg(), ["done"] = true };
            if (All("commit").ToList() is { Count: > 0 } commits)
            {
                args["commits"] = Strs(commits);
            }

            if (F("note") is { } note)
            {
                args["done_note"] = note;
            }

            return args;
        }

        private static JsonArray Strs(IEnumerable<string> items)
        {
            var list = new JsonArray();
            foreach (var item in items)
            {
                list.Add(item);
            }

            return list;
        }

        /// <summary>Runs one write; prints the item id. A conflict (holder or ext held elsewhere) exits 3.</summary>
        private int Write(JsonObject args)
        {
            var result = Invoke(args);
            stdout.Write(result.Member("item")?.AsString() + "\n");
            return 0;
        }

        /// <summary>As <see cref="Write"/>, and a new row is nudged for a description; an <c>--ext</c> that found its row added nothing.</summary>
        private int Add(JsonObject args)
        {
            var result = Invoke(args);
            var item = result.Member("item")?.AsString();
            stdout.Write(item + "\n");
            if (result.Member("existing") is null)
            {
                stderr.WriteLine(HierarchyModule.WorkDescriptionNudge(item ?? string.Empty));
            }

            return 0;
        }

        private JsonValue Invoke(JsonObject args)
        {
            args["dir"] = d.Dir;
            args["by"] = d.By;
            var result = d.Writer.Invoke("progress", HierarchyModule.BacklogWriteOp, JsonText.ToUtf8(args, 0), d.Context, new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));
            if (!result.Ok)
            {
                throw new BacklogFailure(result.Error ?? result.ErrorCode ?? "the write failed", result.ErrorCode == HierarchyModule.BacklogConflictCode ? 3 : 2);
            }

            return JsonText.Parse(result.ResultJson.Span);
        }

        private int Groomed()
        {
            JsonValue summary;
            try
            {
                summary = JsonText.Parse(F("summary") ?? throw new BacklogFailure("groomed needs --summary <json>"));
            }
            catch (JsonParseException e)
            {
                throw new BacklogFailure($"--summary is not JSON: {e.Message}");
            }

            if (!summary.IsMap)
            {
                throw new BacklogFailure("--summary must be a JSON object");
            }

            _ = Invoke(new JsonObject { ["groom"] = summary });
            stdout.Write("backlog: groom recorded\n");
            return 0;
        }

        /// <summary>
        /// <c>import &lt;file.json&gt;</c>: an array of <c>{id?, title, outcome?, theme?, size?, wave?, depends_on?, sources?, ext?, kind?, parent?}</c> (<c>parent</c> is a <c>b&lt;N&gt;</c> already in the backlog). Each element
        /// goes through add, so an element with <c>ext</c> upserts; an element with <c>id</c> is mapped to <c>b&lt;N&gt;</c> and keeps
        /// <c>&lt;file name&gt;:&lt;id&gt;</c> as a source, which makes a re-import skip it. Dependencies are rewritten through the map once every
        /// element is in; an unknown one becomes a <c>question:</c> source and a "waits on" note. The first <c>#NNN</c> source becomes the
        /// GitHub ext of the origin repository.
        /// </summary>
        private int Import()
        {
            var path = Arg(1, "a file");
            JsonValue doc;
            try
            {
                doc = JsonText.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                throw new BacklogFailure($"{path} cannot be read as JSON: {e.Message}");
            }

            if (!doc.IsArray)
            {
                throw new BacklogFailure($"{path} is not a JSON array");
            }

            var prefix = Path.GetFileNameWithoutExtension(path);
            var repo = WatchModule.OriginRepo(d.OriginUrl);
            var elements = doc.AsArray().Where(static e => e.IsMap).ToList();
            string? T(JsonValue e, string k) => e.Member(k) is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
            IReadOnlyList<string> L(JsonValue e, string k) => e.Member(k) is { IsArray: true } a ? [.. a.AsArray().Where(static x => x.IsText).Select(static x => x.AsString())] : [];
            var ids = elements.Select(e => T(e, "id")).OfType<string>().ToHashSet(StringComparer.Ordinal);
            string? IssueRef(JsonValue e) => T(e, "ext") is null && repo is not null
                ? L(e, "sources").FirstOrDefault(static s => s.Length > 1 && s[0] == '#' && s[1..].All(char.IsAsciiDigit))
                : null;

            // two elements whose first #NNN gives the same ext would merge into one item: refused before anything is written
            var derived = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in elements)
            {
                if (IssueRef(e) is { } issue)
                {
                    var who = T(e, "id") ?? T(e, "title") ?? "?";
                    if (!derived.TryAdd(issue, who))
                    {
                        throw new BacklogFailure($"{path}: elements {derived[issue]} and {who} both take ext github:{repo}{issue} from {issue}; nothing imported");
                    }
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var mapped = new List<(JsonValue Element, string Item)>();
            var added = 0;
            var skipped = 0;
            foreach (var e in elements)
            {
                var title = T(e, "title") ?? throw new BacklogFailure($"{path}: an element has no title");
                var args = new JsonObject { ["add"] = title };
                foreach (var key in new[] { "outcome", "theme", "parent" })
                {
                    if (T(e, key) is { } value)
                    {
                        args[key] = value;
                    }
                }

                if (T(e, "kind") is { } kind)
                {
                    args["kind"] = kind.ToLowerInvariant();
                }

                if (T(e, "size") is { } size)
                {
                    args["size"] = size.ToUpperInvariant();
                }

                if (T(e, "wave") is { } wave)
                {
                    args["wave"] = wave.ToLowerInvariant();
                }

                var sources = L(e, "sources").ToList();
                var ext = T(e, "ext");
                if (IssueRef(e) is { } issueRef)
                {
                    ext = $"github:{repo}{issueRef}";
                    sources.Remove(issueRef);
                }

                var unknown = L(e, "depends_on").Where(dep => !ids.Contains(dep)).ToList();
                sources.AddRange(unknown.Select(static q => "question:" + q));
                if (T(e, "id") is { } id)
                {
                    sources.Add($"{prefix}:{id}");
                    args["once"] = $"{prefix}:{id}";
                }

                if (unknown.Count > 0)
                {
                    args["note"] = "waits on " + string.Join(", ", unknown);
                }

                if (sources.Count > 0)
                {
                    args["sources"] = Strs(sources);
                }

                if (ext is not null)
                {
                    SetExt(args, ext, d.OriginUrl);
                }

                var result = Invoke(args);
                var item = result.Member("item")!.Value.AsString();
                if (T(e, "id") is { } elementId)
                {
                    map[elementId] = item;
                }

                mapped.Add((e, item));
                if (result.Member("skipped") is { IsTruthy: true })
                {
                    skipped++;
                }
                else
                {
                    added++;
                }
            }

            // every mapped element, skipped ones too, so a re-run after a partial import still rewrites their depends
            foreach (var (e, item) in mapped)
            {
                var deps = L(e, "depends_on").Where(map.ContainsKey).Select(dep => map[dep]).ToList();
                if (deps.Count > 0 && !deps.SequenceEqual(Read(q => HierarchyModule.BacklogGet(q, d.Dir, item), null)?.Item.Depends ?? []))
                {
                    _ = Invoke(new JsonObject { ["item"] = item, ["depends"] = Strs(deps) });
                }
            }

            stdout.Write($"backlog: imported {added}, skipped {skipped}\n");
            return 0;
        }
    }
}
