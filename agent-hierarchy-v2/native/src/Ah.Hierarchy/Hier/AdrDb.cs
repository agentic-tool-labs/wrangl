using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// <c>progress.work-write</c> with <c>adr</c>: the ADR records' writes (adr-records §4, §5), numbered per repo. No row is ever deleted, so the next number is the highest plus one.
/// A state change that does not fit is refused naming the current state; there is no role check, and <c>by</c> records who wrote.
/// </summary>
internal sealed partial class HierLib
{
    /// <summary>The longest ADR title: a decision is named in one line, often longer than a work item's.</summary>
    internal const int MaxAdrTitle = 240;

    private static readonly string[] AdrSections = ["context", "decision", "consequences"];

    /// <summary>
    /// Args: <c>op</c> (add, the default, settle, supersede, deprecate, link, sync), <c>n</c> for every op but add, and <c>by</c>. Add takes <c>title</c>, <c>status</c>, the sections,
    /// <c>decided_by</c>, <c>source</c>, <c>external</c> with <c>external_sha256</c> and <c>governs</c> (work item numbers). Result: <c>{n}</c>.
    /// </summary>
    private static JsonObject AdrWrite(IDbOpTransaction tx, string dir, JsonValue adr, long ms)
    {
        var op = OpArgs.Text(adr, "op") ?? "add";
        if (op == "add")
        {
            return AddAdr(tx, dir, adr, ms);
        }

        var n = (long)(adr.Member("n") ?? throw new InvalidOperationException("an ADR write needs n")).AsNumber();
        var (title, status) = AdrRow(tx, dir, n);
        var by = OpArgs.Text(adr, "by");
        switch (op)
        {
            case "settle":
                var to = OpArgs.Text(adr, "to");
                if (to is not ("accepted" or "rejected"))
                {
                    throw new InvalidOperationException($"settle {AdrId(n)} {to}: must be accepted or rejected");
                }

                RequireState(n, status, "settle", "proposed");
                if (OpArgs.Text(adr, "decided_by") is { } decidedBy)
                {
                    tx.Execute("UPDATE adrs SET decided_by = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, decidedBy);
                }

                ChangeState(tx, dir, n, title, status, to, OpArgs.Text(adr, "note"), by, ms);
                break;
            case "supersede":
                var newer = (long)(adr.Member("by_n") ?? throw new InvalidOperationException("supersede needs --by")).AsNumber();
                if (newer == n)
                {
                    throw new InvalidOperationException($"{AdrId(n)} cannot supersede itself");
                }

                _ = AdrRow(tx, dir, newer);
                RequireState(n, status, "supersede", "proposed", "accepted");
                tx.Execute("INSERT OR IGNORE INTO adr_links(hier_dir, adr_n, kind, target, by, ts_ms) VALUES (?1, ?2, 'supersedes', ?3, ?4, ?5)", dir, newer, AdrId(n), by, ms);
                ChangeState(tx, dir, n, title, status, "superseded", "by " + AdrId(newer), by, ms);
                break;
            case "deprecate":
                RequireState(n, status, "deprecate", "proposed", "accepted");
                ChangeState(tx, dir, n, title, status, "deprecated", OpArgs.Text(adr, "note"), by, ms);
                break;
            case "link":
                if (LinkAdr(tx, dir, n, adr, by, ms) is { } linked)
                {
                    History(tx, dir, n, status, status, linked, by, ms);
                }

                break;
            case "sync":
                if (tx.Query("SELECT external FROM adrs WHERE hier_dir = ?1 AND n = ?2", dir, n)[0][0] is null)
                {
                    throw new InvalidOperationException($"{AdrId(n)} has no external document to sync: ah adr link {AdrId(n)} --external <path|url>");
                }

                tx.Execute("UPDATE adrs SET external_sha256 = ?3, external_read_ms = ?4, updated_ms = ?4 WHERE hier_dir = ?1 AND n = ?2", dir, n, OpArgs.Text(adr, "external_sha256"), ms);
                foreach (var section in AdrSections)
                {
                    if (AdrSection(adr, section) is { } text)
                    {
                        tx.Execute($"UPDATE adrs SET {section} = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, text);
                    }
                }

                History(tx, dir, n, status, status, "sync", by, ms);
                break;
            default:
                throw new InvalidOperationException($"no ADR op {op}");
        }

        return new JsonObject { ["n"] = n };
    }

    private static JsonObject AddAdr(IDbOpTransaction tx, string dir, JsonValue adr, long ms)
    {
        var title = ArgText(adr, "title");
        if (title.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the title must be one line");
        }

        title = Work.Clean(title).Trim();
        if (title.Length is 0 or > MaxAdrTitle)
        {
            throw new InvalidOperationException($"the title must be 1 to {MaxAdrTitle} characters");
        }

        var status = OpArgs.Text(adr, "status") ?? "proposed";
        if (status is not ("proposed" or "accepted"))
        {
            throw new InvalidOperationException($"--status {status}: must be proposed or accepted");
        }

        var governs = adr.Member("governs") is { IsArray: true } list ? list.AsArray().Select(static g => (long)g.AsNumber()).Distinct().ToList() : [];
        foreach (var item in governs)
        {
            RequireWorkItem(tx, dir, item);
        }

        var n = (long)tx.Query("SELECT COALESCE(MAX(n), 0) + 1 FROM adrs WHERE hier_dir = ?1", dir)[0][0]!;
        var sha = OpArgs.Text(adr, "external_sha256");
        var by = OpArgs.Text(adr, "by");
        tx.Execute(
            "INSERT INTO adrs(hier_dir, n, title, status, context, decision, consequences, decided_by, source, external, external_sha256, external_read_ms, by, created_ms, updated_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?14)",
            dir, n, title, status, AdrSection(adr, "context"), AdrSection(adr, "decision"), AdrSection(adr, "consequences"), OpArgs.Text(adr, "decided_by"), OpArgs.Text(adr, "source"), OpArgs.Text(adr, "external"), sha, sha is null ? null : ms, by, ms);
        foreach (var item in governs)
        {
            tx.Execute("INSERT OR IGNORE INTO adr_links(hier_dir, adr_n, kind, target, by, ts_ms) VALUES (?1, ?2, 'governs', ?3, ?4, ?5)", dir, n, WorkIdIn(tx, dir, item), by, ms);
        }

        History(tx, dir, n, null, status, null, by, ms);
        LogAdr(tx, dir, n, title, status, by, ms);
        return new JsonObject { ["n"] = n };
    }

    /// <summary>
    /// One edge added or removed: <c>governs</c> (a work item number), <c>relates</c> (an ADR number; a self-link is ignored), <c>spec</c> (a path), or the <c>external</c> document.
    /// Returns what changed, as the history note; null when nothing did.
    /// </summary>
    private static string? LinkAdr(IDbOpTransaction tx, string dir, long n, JsonValue adr, string? by, long ms)
    {
        var remove = adr.Member("remove") is { IsTruthy: true };
        var lead = remove ? "unlink " : "link ";
        if (adr.Member("external") is { } external)
        {
            var text = remove ? null : external.AsString();
            var sha = remove ? null : OpArgs.Text(adr, "external_sha256");
            tx.Execute("UPDATE adrs SET external = ?3, external_sha256 = ?4, external_read_ms = ?5, updated_ms = ?6 WHERE hier_dir = ?1 AND n = ?2", dir, n, text, sha, sha is null ? null : ms, ms);
            return lead + "external " + (text ?? string.Empty);
        }

        string kind;
        string target;
        if (adr.Member("governs") is { IsNumber: true } item)
        {
            RequireWorkItem(tx, dir, (long)item.AsNumber());
            (kind, target) = ("governs", WorkIdIn(tx, dir, (long)item.AsNumber()));
        }
        else if (adr.Member("relates") is { IsNumber: true } other)
        {
            if ((long)other.AsNumber() == n)
            {
                return null;
            }

            _ = AdrRow(tx, dir, (long)other.AsNumber());
            (kind, target) = ("relates", AdrId((long)other.AsNumber()));
        }
        else
        {
            (kind, target) = ("spec", OpArgs.Text(adr, "spec") ?? throw new InvalidOperationException("link needs one of --governs, --relates, --spec, --external"));
        }

        tx.Execute(
            remove ? "DELETE FROM adr_links WHERE hier_dir = ?1 AND adr_n = ?2 AND kind = ?3 AND target = ?4" : "INSERT OR IGNORE INTO adr_links(hier_dir, adr_n, kind, target, by, ts_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
            dir, n, kind, target, by, ms);
        tx.Execute("UPDATE adrs SET updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, n, ms);
        return lead + kind + " " + target;
    }

    /// <summary>The new state, its history row with the note, and the decision entry on every governed item. The note never enters the row's sections: they summarise the document.</summary>
    private static void ChangeState(IDbOpTransaction tx, string dir, long n, string title, string from, string state, string? note, string? by, long ms)
    {
        tx.Execute("UPDATE adrs SET status = ?3, updated_ms = ?4 WHERE hier_dir = ?1 AND n = ?2", dir, n, state, ms);
        History(tx, dir, n, from, state, note, by, ms);
        LogAdr(tx, dir, n, title, state, by, ms);
    }

    /// <summary>One row of the ADR's own history, numbered per ADR.</summary>
    private static void History(IDbOpTransaction tx, string dir, long n, string? from, string to, string? note, string? by, long ms)
    {
        var text = note is null ? null : Work.Clean(note).Trim() is { Length: > 0 } clean ? clean : null;
        tx.Execute(
            "INSERT INTO adr_history(hier_dir, n, seq, at, by, from_state, to_state, note) VALUES (?1, ?2, (SELECT COALESCE(MAX(seq), 0) + 1 FROM adr_history WHERE hier_dir = ?1 AND n = ?2), ?3, ?4, ?5, ?6, ?7)",
            dir, n, ms, by, from, to, text);
    }

    /// <summary>One <c>decision</c> entry, <c>adr-N &lt;state&gt;: &lt;title&gt;</c>, on each item the ADR governs, so the item's history is in one place.</summary>
    private static void LogAdr(IDbOpTransaction tx, string dir, long n, string title, string state, string? by, long ms)
    {
        var line = $"{AdrId(n)} {state}: {title}";
        line = line.Length <= Work.MaxTitle ? line : line[..(Work.MaxTitle - 1)] + "…";
        foreach (var row in tx.Query("SELECT target FROM adr_links WHERE hier_dir = ?1 AND adr_n = ?2 AND kind = 'governs'", dir, n))
        {
            var target = (string)row[0]!;
            if (!long.TryParse(target[(target.LastIndexOf('-') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var item)
                || tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, item).Count == 0)
            {
                continue;
            }

            tx.Execute("INSERT INTO work_log(hier_dir, n, ts_ms, type, title, by) VALUES (?1, ?2, ?3, 'decision', ?4, ?5)", dir, item, ms, line, by);
            tx.Execute("UPDATE work_items SET updated_ms = ?3 WHERE hier_dir = ?1 AND n = ?2", dir, item, ms);
        }
    }

    private static string? AdrSection(JsonValue adr, string key)
    {
        if (OpArgs.Text(adr, key) is not { } text || Work.Clean(text).Trim() is not { Length: > 0 } clean)
        {
            return null;
        }

        return Work.BodyProblem(clean) is { } problem ? throw new InvalidOperationException($"{key}: {problem}") : clean;
    }

    private static (string Title, string Status) AdrRow(IDbOpTransaction tx, string dir, long n) =>
        tx.Query("SELECT title, status FROM adrs WHERE hier_dir = ?1 AND n = ?2", dir, n) is [var row]
            ? ((string)row[0]!, (string)row[1]!)
            : throw new InvalidOperationException("no such ADR " + AdrId(n));

    private static void RequireState(long n, string status, string verb, params string[] allowed)
    {
        if (!allowed.Contains(status, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{AdrId(n)} is {status}; {verb} needs {string.Join(" or ", allowed)}");
        }
    }

    private static void RequireWorkItem(IDbOpTransaction tx, string dir, long item)
    {
        if (tx.Query("SELECT 1 FROM work_items WHERE hier_dir = ?1 AND n = ?2", dir, item).Count == 0)
        {
            throw new InvalidOperationException($"no work item {item}");
        }
    }

    private static string AdrId(long n) => "adr-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
