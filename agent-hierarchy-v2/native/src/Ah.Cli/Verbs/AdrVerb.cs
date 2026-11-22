using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah adr</c>: durable decisions as records in the state database (adr-records §5), keyed by the repo's hierarchy dir as work items are. Exit 0 ok, 1 nothing listed, 2 a usage
/// error: a malformed id, no such ADR, a state change that does not fit, or a write the registry refused. There is no role check: <c>by</c> records who ran it.
/// </summary>
internal static class AdrVerb
{
    private const string Usage =
        "usage: ah adr add <title> [--status proposed|accepted] [--context s] [--decision s] [--consequences s] [--body-file -] [--governs atp-N]... [--source s] [--decided-by s] [--external path|url]"
        + " | show <id> [--json] | list [--status s] [--governs atp-N] [--waiting] [--json] | settle <id> accepted|rejected [--note s] [--decided-by s] | ask <id> | supersede <old> --by <new>"
        + " | deprecate <id> [--note s] | link <id> (--governs atp-N | --relates adr-N | --spec path | --external path|url) [--remove] | sync <id> [--context s] [--decision s] [--consequences s]"
        + "   (all take [--cwd dir]; every verb but supersede takes [--by name])";

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "status", "context", "decision", "consequences", "body-file", "governs", "source", "decided-by", "external", "cwd", "by", "note", "relates", "spec",
    };

    private static readonly HashSet<string> Bools = new(StringComparer.Ordinal) { "json", "waiting", "remove" };

    private static readonly string[] Sections = ["context", "decision", "consequences"];

    private sealed class AdrFailure(string message, int code = 2) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private sealed record Call(CliContext Context, ServiceGraph G, string Cwd, string Dir, string Key, List<string> Words, Dictionary<string, string> Flags, List<string> Governs, TextWriter Stdout, TextWriter Stderr)
    {
        public string? F(string key) => Flags.GetValueOrDefault(key);

        public bool Has(string key) => Flags.ContainsKey(key);

        public string Word(int i, string what) => Words.Count > i ? Words[i] : throw new AdrFailure($"{Words[0]} needs {what}; {Usage}");

        // supersede's --by names the newer ADR, so its caller is the session's own name
        public string By => (Words[0] != "supersede" ? F("by") : null) ?? BacklogVerb.Caller(Context.Env, G, Dir);
    }

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah adr: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        var words = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var governs = new List<string>();
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
                flags[key] = "true";
            }
            else if (Values.Contains(key) && i + 1 < args.Length)
            {
                if (key == "governs")
                {
                    governs.Add(args[++i]);
                }
                else
                {
                    flags[key] = args[++i];
                }
            }
            else
            {
                stderr.WriteLine($"ah adr: unrecognized flag --{key}; {Usage}");
                return 2;
            }
        }

        var cwd = flags.GetValueOrDefault("cwd") ?? Directory.GetCurrentDirectory();
        var dir = BacklogVerb.DirOf(g, cwd);
        var call = new Call(context, g, cwd, dir, HierarchyModule.WorkDirKey(dir), words, flags, governs, stdout, stderr);
        try
        {
            return words.FirstOrDefault() switch
            {
                "add" => Add(call),
                "show" => Show(call),
                "list" => List(call),
                "settle" => Settle(call),
                "ask" => Ask(call),
                "supersede" => Supersede(call),
                "deprecate" => Deprecate(call),
                "link" => Link(call),
                "sync" => Sync(call),
                _ => throw new AdrFailure(Usage),
            };
        }
        catch (AdrFailure failure)
        {
            stderr.WriteLine("ah adr: " + failure.Message);
            return failure.Code;
        }
    }

    private static int Add(Call c)
    {
        var adr = new JsonObject { ["title"] = c.Word(1, "a title"), ["by"] = c.By };
        if (c.F("status") is { } status)
        {
            adr["status"] = status;
        }

        // a flag wins over its section of --body-file
        var body = c.F("body-file") is { } source ? BodySections(ReadBody(source, c.Context)) : [];
        foreach (var section in Sections)
        {
            if ((c.F(section) ?? body.GetValueOrDefault(section)) is { } text)
            {
                adr[section] = text;
            }
        }

        foreach (var (flag, key) in new[] { ("source", "source"), ("decided-by", "decided_by") })
        {
            if (c.F(flag) is { } text)
            {
                adr[key] = text;
            }
        }

        if (c.F("external") is { } external)
        {
            SetExternal(adr, c.Cwd, external);
        }

        var items = new JsonArray();
        foreach (var text in c.Governs)
        {
            items.Add(ItemOf(c, text));
        }

        adr["governs"] = items;
        c.Stdout.Write(Id(Write(c, adr)) + "\n");
        return 0;
    }

    private static int Settle(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var adr = new JsonObject { ["op"] = "settle", ["n"] = n, ["to"] = c.Word(2, "accepted or rejected"), ["by"] = c.By };
        Copy(c, adr, ("note", "note"), ("decided-by", "decided_by"));
        return Changed(c, adr, n);
    }

    /// <summary><c>ask &lt;id&gt;</c> (b195): parks a question for the user to settle a proposed ADR; their option answer on Needs-you settles it.</summary>
    private static int Ask(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var (title, status) = Read(c, r => r.Query("SELECT title, status FROM adrs WHERE hier_dir = ?1 AND n = ?2", c.Key, n) is [var row] ? ((string)row[0]!, (string)row[1]!) : throw new AdrFailure("no such ADR " + Id(n)));
        if (status != "proposed")
        {
            throw new AdrFailure($"ask {Id(n)}: it is {status}; only a proposed ADR is asked");
        }

        return Msg.MsgCommand.AdrAsk(c.Context, c.G, c.Cwd, n, title, c.Stdout, c.Stderr);
    }

    /// <summary>
    /// b195: settles ADR <paramref name="n"/> to <paramref name="to"/> from the user's answer to question <paramref name="id"/> on Needs-you, recorded
    /// <c>decided_by = user</c>, <c>by = ui-answer</c>. Returns the text the Orchestrator's question-answered event gains: settled, or why not.
    /// </summary>
    internal static string SettleFromAnswer(ServiceGraph g, string dir, long n, string to, string id)
    {
        var adr = new JsonObject { ["op"] = "settle", ["n"] = n, ["to"] = to, ["decided_by"] = "user", ["by"] = "ui-answer", ["note"] = $"the user's answer to {id}" };
        var result = Invoke(g, BacklogVerb.DirOf(g, dir), adr);
        return result.Ok ? $"; {Id(n)} settled {to}" : $"; {Id(n)} not settled: {result.Error ?? result.ErrorCode ?? "the write failed"}";
    }

    private static int Supersede(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var newer = ParseId(c.F("by") ?? throw new AdrFailure($"supersede needs --by <new>; {Usage}"));
        return Changed(c, new JsonObject { ["op"] = "supersede", ["n"] = n, ["by_n"] = newer, ["by"] = c.By }, n);
    }

    private static int Deprecate(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var adr = new JsonObject { ["op"] = "deprecate", ["n"] = n, ["by"] = c.By };
        Copy(c, adr, ("note", "note"));
        return Changed(c, adr, n);
    }

    /// <summary>A state change, then its new state and the document warning: a changed document is flagged, never refused.</summary>
    private static int Changed(Call c, JsonObject adr, long n)
    {
        _ = Write(c, adr);
        var row = Read(c, r => r.Query("SELECT status, external, external_sha256, external_read_ms FROM adrs WHERE hier_dir = ?1 AND n = ?2", c.Key, n)[0]);
        c.Stdout.Write($"{Id(n)} {row[0]}\n");
        if (DocumentFlag(Id(n), row[1] as string, row[2] as string, row[3] as long?) is { } flag)
        {
            c.Stderr.WriteLine("ah adr: " + flag);
        }

        return 0;
    }

    private static int Link(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var adr = new JsonObject { ["op"] = "link", ["n"] = n, ["by"] = c.By };
        if (c.Has("remove"))
        {
            adr["remove"] = true;
        }

        var given = new[] { c.Governs.Count > 0, c.Has("relates"), c.Has("spec"), c.Has("external") }.Count(static b => b);
        if (given != 1 || c.Governs.Count > 1)
        {
            throw new AdrFailure($"link needs exactly one of --governs, --relates, --spec, --external; {Usage}");
        }

        string what;
        if (c.Governs.Count == 1)
        {
            adr["governs"] = ItemOf(c, c.Governs[0]);
            what = "governs " + c.Governs[0];
        }
        else if (c.F("relates") is { } relates)
        {
            adr["relates"] = ParseId(relates);
            what = "relates " + relates;
        }
        else if (c.F("spec") is { } spec)
        {
            adr["spec"] = spec;
            what = "spec " + spec;
        }
        else
        {
            if (c.Has("remove"))
            {
                adr["external"] = string.Empty;
            }
            else
            {
                SetExternal(adr, c.Cwd, c.F("external")!);
            }

            what = "external " + (c.Has("remove") ? string.Empty : c.F("external"));
        }

        _ = Write(c, adr);
        c.Stdout.Write($"{Id(n)} {(c.Has("remove") ? "no longer " : string.Empty)}{what.TrimEnd()}\n");
        return 0;
    }

    /// <summary>Re-reads the document: its hash and the time are recorded, and any section given replaces the summary's.</summary>
    private static int Sync(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var external = Read(c, r => r.Query("SELECT external FROM adrs WHERE hier_dir = ?1 AND n = ?2", c.Key, n) is [var row] ? row[0] as string : throw new AdrFailure("no such ADR " + Id(n)));
        var adr = new JsonObject { ["op"] = "sync", ["n"] = n, ["by"] = c.By };
        if (external is not null && Path.IsPathRooted(external) && Sha256(external) is { } sha)
        {
            adr["external_sha256"] = sha;
        }

        Copy(c, adr, ("context", "context"), ("decision", "decision"), ("consequences", "consequences"));
        _ = Write(c, adr);
        c.Stdout.Write($"{Id(n)} synced\n");
        return 0;
    }

    /// <summary>A proposed ADR waits on the user when the user decides it or nobody was named; <c>jim</c> is the decider older records carry for the user.</summary>
    private const string WaitingSql = "status = 'proposed' AND (decided_by IS NULL OR decided_by IN ('user', 'jim'))";

    /// <summary>The decider a proposed ADR waits on the user for, as <see cref="WaitingSql"/> reads it.</summary>
    internal static bool WaitsOnUser(string? status, string? decidedBy) => status == "proposed" && decidedBy is null or "user" or "jim";

    private const int WaitingMax = 10;

    /// <summary>
    /// The Orchestrator's SessionStart list of ADRs waiting on the user (adr-records §7): a count and up to <see cref="WaitingMax"/> titles, newest first. Null when none waits or the store
    /// cannot be read: the session always starts.
    /// </summary>
    internal static string? Waiting(string ahHome, string dirKey)
    {
        if (StateDbModule.OpenReader(ahHome).Reader is not { } reader)
        {
            return null;
        }

        using (reader)
        {
            List<object?[]> rows;
            try
            {
                rows = [.. reader.Query($"SELECT n, title FROM adrs WHERE hier_dir = ?1 AND {WaitingSql} ORDER BY n DESC", dirKey)];
            }
            // the reader rethrows the store's own (internal) SqliteException, busy included: any store failure only drops this list, never the session's rules
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                return null;
            }

            if (rows.Count == 0)
            {
                return null;
            }

            var text = new StringBuilder($"ADRs waiting on you ({rows.Count}), until settled: relay the user's answer with ah adr settle <id> accepted|rejected --decided-by user; ah adr list --waiting shows them.");
            foreach (var row in rows.Take(WaitingMax))
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"\n- {Id((long)row[0]!)} {row[1]}");
            }

            if (rows.Count > WaitingMax)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"\n+{rows.Count - WaitingMax} more");
            }

            return text.ToString();
        }
    }

    private static int List(Call c)
    {
        var status = c.F("status");
        var waiting = c.Has("waiting");
        var target = c.Governs.Count > 0 ? WorkIdOf(c, c.Governs[0]) : null;
        var rows = Read(c, r => r.Query(
            $"SELECT n, title, status, decided_by FROM adrs a WHERE hier_dir = ?1 AND (?2 IS NULL OR status = ?2) AND (?3 = 0 OR ({WaitingSql}))"
            + " AND (?4 IS NULL OR EXISTS(SELECT 1 FROM adr_links l WHERE l.hier_dir = a.hier_dir AND l.adr_n = a.n AND l.kind = 'governs' AND l.target = ?4)) ORDER BY n DESC",
            c.Key, status, waiting ? 1 : 0, target));
        if (c.Has("json"))
        {
            var list = new JsonArray();
            foreach (var row in rows)
            {
                list.Add(new JsonObject { ["id"] = Id((long)row[0]!), ["title"] = (string)row[1]!, ["status"] = (string)row[2]!, ["decided_by"] = Text(row[3] as string) });
            }

            c.Stdout.Write(JsonText.Stringify(new JsonObject { ["adrs"] = list }, 0) + "\n");
        }
        else
        {
            foreach (var row in rows)
            {
                c.Stdout.Write($"{Id((long)row[0]!)} ({row[2]}{(WaitsOnUser(row[2] as string, row[3] as string) ? ", waiting on you" : string.Empty)}) {row[1]}\n");
            }
        }

        return rows.Count == 0 ? 1 : 0;
    }

    private static int Show(Call c)
    {
        var n = ParseId(c.Word(1, "an id"));
        var id = Id(n);
        return Read(c, reader =>
        {
            if (reader.Query(
                "SELECT title, status, context, decision, consequences, decided_by, source, external, external_sha256, external_read_ms, by, created_ms, updated_ms FROM adrs WHERE hier_dir = ?1 AND n = ?2",
                c.Key, n) is not [var r])
            {
                throw new AdrFailure("no such ADR " + id);
            }

            string? S(int i) => r[i] as string;
            long? L(int i) => r[i] as long?;
            var links = reader.Query("SELECT kind, target FROM adr_links WHERE hier_dir = ?1 AND adr_n = ?2 ORDER BY kind, target", c.Key, n).Select(static l => ((string)l[0]!, (string)l[1]!)).ToList();
            var from = reader.Query("SELECT adr_n, kind FROM adr_links WHERE hier_dir = ?1 AND target = ?2 ORDER BY adr_n", c.Key, id).Select(static l => (Id((long)l[0]!), (string)l[1]!)).ToList();

            var history = reader.Query("SELECT at, by, from_state, to_state, note FROM adr_history WHERE hier_dir = ?1 AND n = ?2 ORDER BY seq", c.Key, n)
                .Select(static h => (At: (long)h[0]!, By: h[1] as string, From: h[2] as string, To: h[3] as string, Note: h[4] as string)).ToList();
            var external = S(7);
            var document = external is null ? null : Document(external, S(8));
            if (c.Has("json"))
            {
                var record = new JsonObject
                {
                    ["id"] = id, ["title"] = S(0)!, ["status"] = S(1)!, ["decided_by"] = Text(S(5)), ["source"] = Text(S(6)), ["by"] = Text(S(10)),
                    ["created_ms"] = L(11)!.Value, ["updated_ms"] = L(12)!.Value,
                    ["external"] = Text(external), ["external_sha256"] = Text(S(8)), ["external_read_ms"] = L(9) is { } read ? read : JsonValue.Null, ["document"] = Text(document),
                };

                // the document is authoritative: the row's text is only a summary of it
                var summary = external is null ? record : new JsonObject { ["authoritative"] = external };
                for (var i = 0; i < Sections.Length; i++)
                {
                    summary[Sections[i]] = Text(S(2 + i));
                }

                if (external is not null)
                {
                    record["summary"] = summary;
                }

                var linkArray = new JsonArray();
                foreach (var (kind, target) in links)
                {
                    linkArray.Add(new JsonObject { ["kind"] = kind, ["target"] = target });
                }

                var fromArray = new JsonArray();
                foreach (var (source, kind) in from)
                {
                    fromArray.Add(new JsonObject { ["id"] = source, ["kind"] = kind });
                }

                var historyArray = new JsonArray();
                foreach (var h in history)
                {
                    historyArray.Add(new JsonObject { ["at"] = h.At, ["by"] = Text(h.By), ["from_state"] = Text(h.From), ["to_state"] = Text(h.To), ["note"] = Text(h.Note) });
                }

                record["links"] = linkArray;
                record["linked_from"] = fromArray;
                record["history"] = historyArray;
                c.Stdout.Write(JsonText.Stringify(record, 0) + "\n");
                return 0;
            }

            var text = new StringBuilder();
            _ = text.Append(CultureInfo.InvariantCulture, $"{id} {S(0)} ({S(1)})\n");
            _ = text.Append(CultureInfo.InvariantCulture, $"decided by: {S(5) ?? "-"}   source: {S(6) ?? "-"}   by: {S(10) ?? "-"}   created: {Date(L(11)!.Value)}   updated: {Date(L(12)!.Value)}\n");
            if (DocumentFlag(id, external, S(8), L(9)) is { } flag)
            {
                _ = text.Append(flag).Append('\n');
            }

            if (external is not null)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"summary (authoritative: {external})\n");
            }

            for (var i = 0; i < Sections.Length; i++)
            {
                if (S(2 + i) is { } section)
                {
                    _ = text.Append(CultureInfo.InvariantCulture, $"{Sections[i]}:\n{section}\n");
                }
            }

            foreach (var (kind, target) in links)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"{kind} {target}\n");
            }

            foreach (var (source, kind) in from)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"{source} {kind} {id}\n");
            }

            foreach (var h in history)
            {
                var change = h.From is null ? "added " + h.To : h.From == h.To ? h.To : h.From + " → " + h.To;
                _ = text.Append(CultureInfo.InvariantCulture, $"{Date(h.At)} {change}{(h.Note is null ? string.Empty : ": " + h.Note)} ({h.By ?? "-"})\n");
            }

            c.Stdout.Write(text.ToString());
            return 0;
        });
    }

    private static long Write(Call c, JsonObject adr)
    {
        var result = Invoke(c.G, c.Dir, adr);
        if (!result.Ok)
        {
            throw new AdrFailure(result.Error ?? result.ErrorCode ?? "the write failed");
        }

        return (long)(JsonText.Parse(result.ResultJson.Span).Member("n") ?? throw new AdrFailure("the write answered no number")).AsNumber();
    }

    private static StateResult Invoke(ServiceGraph g, string dir, JsonObject adr) =>
        g.Writer.Invoke(
            "progress", HierarchyModule.WorkWriteOp, JsonText.ToUtf8(new JsonObject { ["dir"] = dir, ["team"] = string.Empty, ["adr"] = adr }, 0), g.Hierarchy.Messages.PoolContext(dir), new StateCallOptions(OperationId: Guid.NewGuid().ToString("N")));

    private static T Read<T>(Call c, Func<IStateDbReader, T> body)
    {
        if (StateDbModule.OpenReader(c.Context.Env.AhHome).Reader is not { } reader)
        {
            throw new AdrFailure("the state database cannot be read");
        }

        using (reader)
        {
            return body(reader);
        }
    }

    private static void Copy(Call c, JsonObject adr, params (string Flag, string Key)[] pairs)
    {
        foreach (var (flag, key) in pairs)
        {
            if (c.F(flag) is { } text)
            {
                adr[key] = text;
            }
        }
    }

    private static long ItemOf(Call c, string text) =>
        WorkVerb.Resolve(c.G, c.Context.Env.AhHome, c.Dir, text, WorkVerb.CallerTeam(c.Context.Env)).Row is { } row ? row.N : throw new AdrFailure($"--governs {text}: no work item {text}");

    private static string WorkIdOf(Call c, string text)
    {
        var n = ItemOf(c, text);
        return HierarchyModule.WorkId(Read(c, r => HierarchyModule.WorkPrefix(r.Query, c.Dir)) ?? "w", n);
    }

    /// <summary>A URL (a scheme before any slash) is stored as given and never hashed; a path is stored absolute, with its SHA-256 when the file is there to read.</summary>
    private static void SetExternal(JsonObject adr, string cwd, string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && !text[..colon].Contains('/', StringComparison.Ordinal))
        {
            adr["external"] = text;
            return;
        }

        var full = Path.GetFullPath(text, cwd);
        adr["external"] = full;
        if (Sha256(full) is { } sha)
        {
            adr["external_sha256"] = sha;
        }
    }

    private static string? Sha256(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadBody(string source, CliContext context)
    {
        try
        {
            using var input = source == "-" ? (context.ReadStdin is { } read ? new MemoryStream(read().ToArray()) : Console.OpenStandardInput()) : File.OpenRead(source);
            using var reader = new StreamReader(input, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AdrFailure($"--body-file {source}: {e.Message}");
        }
    }

    /// <summary>The text under each <c>## Context</c>, <c>## Decision</c> and <c>## Consequences</c> heading; any other heading ends a section and is dropped.</summary>
    private static Dictionary<string, string> BodySections(string body)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        string? at = null;
        var text = new StringBuilder();
        foreach (var line in body.Split('\n').Append("## "))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (at is not null && text.ToString().Trim() is { Length: > 0 } done)
                {
                    found[at] = done;
                }

                var name = line[3..].Trim().ToLowerInvariant();
                at = Sections.Contains(name) ? name : null;
                _ = text.Clear();
            }
            else if (at is not null)
            {
                _ = text.Append(line).Append('\n');
            }
        }

        return found;
    }

    internal static string Id(long n) => "adr-" + n.ToString(CultureInfo.InvariantCulture);

    private static long ParseId(string text) =>
        text.StartsWith("adr-", StringComparison.Ordinal) && text.Length is > 4 and <= 22 && text[4..].All(char.IsAsciiDigit) && long.TryParse(text[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new AdrFailure($"{text}: not an ADR id (adr-<n>)");

    /// <summary>The line a reader of the record is told about its document, or null when there is nothing to tell (no document, a URL, or an unchanged file).</summary>
    private static string? DocumentFlag(string id, string? external, string? recorded, long? readMs) =>
        external is null ? null : Document(external, recorded) switch
        {
            "changed" => $"document changed since {(readMs is { } at ? Date(at) : "it was added")}: ah adr sync {id}",
            "missing" => "document missing",
            _ => null,
        };

    /// <summary>What a reader of <paramref name="external"/> is told: <c>url</c> (never checked), <c>missing</c>, <c>changed</c> (the hash differs from the one recorded, or none was), or <c>ok</c>.</summary>
    internal static string Document(string external, string? recorded)
    {
        if (!Path.IsPathRooted(external))
        {
            return "url";
        }

        if (!File.Exists(external))
        {
            return "missing";
        }

        return Sha256(external) is { } now && now == recorded ? "ok" : "changed";
    }

    private static JsonValue Text(string? text) => text is null ? JsonValue.Null : text;

    internal static string Date(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
