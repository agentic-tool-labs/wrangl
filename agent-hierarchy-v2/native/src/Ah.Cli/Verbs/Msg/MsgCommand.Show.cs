using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    private static readonly string[] HandleTypes = ["request", "response", "note"];

    /// <summary>
    /// The two lines a request about a known item ends with: the brief's only instruction about logging, so it reaches every role and no agent file
    /// carries it. The item is the request's <c>item:</c>, else the one item that lists it. A failed lookup prints no footer and never fails the show.
    /// </summary>
    private string? WorkFooter(string path, string id)
    {
        JsonValue? fm;
        string pool;
        string team;
        string text;
        try
        {
            var read = Hier.Core.ReadMsgFile(path);
            fm = read.Member("fm");
            text = read.Member("text") is { IsText: true } t ? t.AsString() : string.Empty;
            if (FrontText(fm?.Member("type")) != "request")
            {
                return null;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }

        // RP-D8: the addressee role's parameters, for any request to it, team and item or none; an error shows no block
        string? parameters;
        try
        {
            parameters = FrontText(fm?.Member("to")) is { } role ? Ah.Config.ConfigModule.ParamBlock(_graph.Config.RoleParams(Path.GetDirectoryName(path), role)) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            parameters = null;
        }

        try
        {
            (pool, team) = ChecklistScope(fm);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return parameters;
        }

        // ah2-53: every request's footer starts with its team's goal line; a request with no team names none, and a goal that cannot be read prints nothing
        string? goal;
        try
        {
            goal = FrontText(fm?.Member("team")) is { } named ? RulesReads.GoalLine(_env.AhHome, pool, RulesReads.TeamChain(_graph, pool, named)) : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            goal = null;
        }

        string? work;
        try
        {
            var item = FrontText(fm?.Member("item"));
            if (item is null && HierarchyModule.ChecklistItemOfRequest(HierarchyModule.ChecklistEntries(Hier.Progress, pool, team), id) is { } entry)
            {
                item = HierarchyModule.ChecklistKey(entry);
            }

            work = item is null ? null : WorkVerb.Footer(_graph, _env.AhHome, pool, item, team);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            work = null;
        }

        // ah2-54 S3: the lessons block comes after the goal and work lines; a request with no matching lesson, or lessons that cannot be read, add nothing
        string? lessons;
        try
        {
            lessons = LessonsFooter.Render(LessonsFooter.ForRequest(_graph, _env.AhHome, pool, fm, text), id);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            lessons = null;
        }

        var footer = goal is null ? work : goal + "\n" + work;
        if (lessons is not null)
        {
            footer += lessons;
        }

        // ah2-504: the role-params block comes last, after the goal, work and lessons lines
        return parameters is null ? footer : footer is null ? parameters : footer + "\n" + parameters;
    }

    /// <summary>
    /// <c>msg show &lt;id&gt; [request|response|note] [--section n]… [--out file [--force]] [--to name] [--pool dir]</c>: prints the message,
    /// frontmatter and body, raw; with <c>--section</c> only the frontmatter and those <c>## [n]</c> sections.
    /// </summary>
    private int Show()
    {
        var (path, id) = HandleFromPositional("show");
        var text = MessageTextAt(path, id);
        var sections = _opts.Texts("section");
        if (sections.Count > 0)
        {
            text = OnlySections(text, sections, id);
        }

        if (_opts.Text("out") is not { Length: > 0 } outFile)
        {
            if (_opts.Has("out"))
            {
                throw _out.Fail("--out needs a file path");
            }

            _stdout.Write(text);
            if (WorkFooter(path, id) is { } footer)
            {
                _stdout.Write((text.EndsWith('\n') ? string.Empty : "\n") + footer);
            }

            return 0;
        }

        var target = Path.GetFullPath(outFile, _cwd);
        var parent = Path.GetDirectoryName(target) ?? string.Empty;
        if (InMessageStore(parent))
        {
            throw _out.Fail($"--out: {target} is inside a message store directory; write the copy somewhere else");
        }

        if (Directory.Exists(target))
        {
            throw _out.Fail($"--out: {target} is a directory; name a file");
        }

        if (File.Exists(target) && !_opts.Has("force"))
        {
            throw _out.Fail($"--out: {target} exists; pass --force to replace it");
        }

        var tmp = Path.Combine(parent, $".{Path.GetFileName(target)}.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
        _out.Text($"{id} → {target}");
        return 0;
    }

    /// <summary>The message key and id named by the positional handle <c>&lt;id&gt; [type]</c>, or by a legacy absolute path.</summary>
    private (string Path, string Id) HandleFromPositional(string verb)
    {
        if (_opts.Positional.Count == 0 || _opts.Positional[0].Length == 0)
        {
            throw _out.Fail($"{verb} needs a message id: ah msg {verb} <id> [request|response|note]");
        }

        var first = _opts.Positional[0];
        if (first.Contains('/') || first.EndsWith(".md", StringComparison.Ordinal))
        {
            var legacy = Path.GetFullPath(first, _cwd);
            return (legacy, KeyPart(legacy, 0) ?? legacy);
        }

        var type = _opts.Positional.Count > 1 ? _opts.Positional[1] : "request";
        if (!HandleTypes.Contains(type))
        {
            throw _out.Fail($"{verb}: type must be request, response or note, got {Quote(type)}");
        }

        return (ResolveHandle(first, type), first);
    }

    /// <summary>
    /// The key of message <paramref name="id"/> of <paramref name="type"/>, through the core's token resolver: the caller's pool breaks a tie, then
    /// <c>--to</c>; <c>--pool</c> and <c>--to</c> also must match. None is <c>no {type} {id}</c> (exit 1); more than one lists each candidate.
    /// </summary>
    private string ResolveHandle(string id, string type, bool quiet = false)
    {
        MsgExit NotFound(string message) => quiet ? new MsgExit(1) : this.NotFound(message);
        if (!Hier.Messages.IsMessageId(id))
        {
            throw _out.Fail($"{Quote(id)} is not a message id");
        }

        var to = _opts.Text("to");
        var pool = _opts.Text("pool") is { Length: > 0 } p ? Path.GetFullPath(p, _cwd) : null;
        var hit = Hier.Core.ResolveMsgId(id, type, HierarchyDir, pool, to);

        bool Fits(string? rowTo, string rowPool) => (to is null || rowTo == to) && (pool is null || SamePath(rowPool, pool));
        if (hit.Key is { } key)
        {
            return Fits(KeyPart(key, 1), key[..Math.Max(0, key.IndexOf("/msgs/", StringComparison.Ordinal))]) ? key : throw NotFound($"no {type} {id}");
        }

        if (hit.Candidates.Count > 0)
        {
            var fits = hit.Candidates.Where(c => Fits(c.To, c.Pool)).ToList();
            var listed = string.Join("; ", (fits.Count > 0 ? fits : hit.Candidates).Select(c => $"to {c.To} in pool {c.Pool}"));
            throw fits.Count == 0 && (to is not null || pool is not null)
                ? NotFound($"no {type} {id}")
                : _out.Fail($"{type} {id} is in more than one place ({listed}); pick one with --to <name> or --pool <dir>");
        }

        throw NotFound(quiet ? string.Empty : EntryRedirect(id) ?? $"no {type} {id}");
    }

    /// <summary>
    /// ah2-78: when <paramref name="id"/> is a mailbox entry of the caller's that carries a message, the line naming that message and how to
    /// read it; null otherwise. A box that cannot be read names nothing.
    /// </summary>
    private string? EntryRedirect(string id)
    {
        try
        {
            foreach (var box in Me())
            {
                foreach (var entry in _graph.Mailbox.ReadEntries(box.Dir).Entries)
                {
                    if (entry.Member("id") is { IsText: true } entryId && entryId.AsString() == id
                        && entry.Member("ref") is { IsText: true } reference && MsgPointer.Handle(reference.AsString()) is { } handle)
                    {
                        return $"{id} is a mailbox entry for {handle}; run `ah msg show {handle}`";
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The caller's pool (or <c>--pool</c>) and every pool a handle may resolve in: the caller's and the main checkout's teams' pools.</summary>
    private (string Home, IReadOnlyList<string> Every) Pools()
    {
        if (_opts.Text("pool") is { Length: > 0 } pool)
        {
            var only = Path.GetFullPath(pool, _cwd);
            return (only, [only]);
        }

        var home = HierarchyDir;
        var homes = Config.MainHierarchyDir(_cwd) is { Length: > 0 } main && !SamePath(main, home) ? new[] { home, main } : [home];
        return (home, homes.SelectMany(h => Hier.Messages.PoolDirs(h, [.. RosterCore.ListTeamNames(h), "team"])).Prepend(home).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>Whether <paramref name="dir"/>, resolved, is a pool's <c>msgs/</c> directory or anywhere below it (<c>msgs/archive/</c> included).</summary>
    private bool InMessageStore(string dir)
    {
        var cmp = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Real(string p)
        {
            try
            {
                return _graph.Io.Files.RealPath(p);
            }
            catch (Exception)
            {
                return Path.GetFullPath(p);
            }
        }

        var real = Real(dir).TrimEnd('/') + "/";
        return Pools().Every.Select(p => Real(Path.Combine(p, "msgs")).TrimEnd('/') + "/").Any(store => real.StartsWith(store, cmp));
    }

    /// <summary>Part <paramref name="index"/> (0 id, 1 to, 2 slug, 3 type) of a message key's file name <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--&lt;type&gt;.md</c>.</summary>
    private static string? KeyPart(string path, int index)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = name[..^3].Split("--");
        return parts.Length < 4 ? null : index == 3 ? parts[^1] : index == 2 ? string.Join("--", parts[2..^1]) : parts[index];
    }

    private string MessageTextAt(string path, string id)
    {
        var parsed = Hier.Core.ReadMsgFile(path);
        if (parsed.IsNull || !parsed.IsMap || parsed.Member("text") is not { IsText: true } text)
        {
            throw NotFound($"no body for {id}");
        }

        return text.AsString();
    }

    /// <summary>
    /// b476: the key a note's bare <c>--ref</c> id names: a note of that id, else the request, else the response (a note often refers to an
    /// exchange). Only the last miss is reported.
    /// </summary>
    private string ResolveNoteRef(string id)
    {
        foreach (var type in (string[])["note", "request"])
        {
            try
            {
                return ResolveHandle(id, type, quiet: true);
            }
            catch (MsgExit)
            {
            }
        }

        return ResolveHandle(id, "response");
    }

    private MsgExit NotFound(string message)
    {
        _stderr.Write($"ah msg: {message}\n");
        return new MsgExit(1);
    }

    /// <summary>The line index just past the closing frontmatter fence; 0 when the text has no frontmatter.</summary>
    private static int BodyStart(string[] lines)
    {
        if (lines.Length == 0 || lines[0] != "---")
        {
            return 0;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---")
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>The frontmatter plus the <c>[n]</c> sections named, each marker through to the next section marker, in document order.</summary>
    private string OnlySections(string text, IReadOnlyList<string> wanted, string id)
    {
        var lines = text.Split('\n');
        var start = BodyStart(lines);
        var markers = HierarchyModule.SectionMarkers(lines);
        var kept = lines.Take(start).ToList();
        var keep = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < lines.Length; i++)
        {
            if (markers[i] is { } marker)
            {
                var n = wanted.FirstOrDefault(w => w == marker.N);
                keep = n is not null;
                if (n is not null)
                {
                    seen.Add(n);
                }
            }

            if (keep)
            {
                kept.Add(lines[i]);
            }
        }

        if (wanted.FirstOrDefault(w => !seen.Contains(w)) is { } missing)
        {
            throw NotFound($"{id} has no section [{missing}]");
        }

        var joined = string.Join('\n', kept);
        return joined.EndsWith('\n') ? joined : joined + "\n";
    }

    /// <summary>The body of <paramref name="text"/> with section <c>## [n]</c>'s body replaced by <paramref name="replacement"/>, or the section appended when absent.</summary>
    private static string SplicedBody(string text, string n, string replacement)
    {
        var lines = text.Split('\n');
        var body = lines.Skip(BodyStart(lines)).ToList();
        var section = replacement.EndsWith('\n') ? replacement : replacement + "\n";
        var markers = HierarchyModule.SectionMarkers(body);
        var at = Array.FindIndex(markers, m => m?.N == n);
        if (at < 0)
        {
            var tail = string.Join('\n', body).TrimEnd('\n');
            return (tail.Length == 0 ? string.Empty : tail + "\n") + $"## [{n}]\n" + section;
        }

        var end = Array.FindIndex(markers, at + 1, static m => m is not null);
        var before = string.Join('\n', body.Take(at + 1)) + "\n";
        var after = end < 0 ? string.Empty : string.Join('\n', body.Skip(end));
        return before + section + after;
    }
}
