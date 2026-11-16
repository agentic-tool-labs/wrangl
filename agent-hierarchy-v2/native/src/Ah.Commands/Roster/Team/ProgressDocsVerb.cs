using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// <c>ah progress list [--team T]</c> and <c>ah progress show &lt;team&gt; [&lt;stream&gt;] [--journal|--rollup|--closing] [--json]</c>,
/// <c>ah progress show --key &lt;old path&gt;</c> (per-dir-files-to-db S11d): the progress documents live in the state database, and
/// these verbs are how a person reads them. They only read, so the owner guard lets them through.
/// </summary>
internal static class ProgressDocsVerb
{
    private const string Usage =
        "usage: ah progress list [--team T] | ah progress off|on <team> | ah progress show <team> [<stream>] [--journal|--rollup|--closing] [--json] | ah progress show --key <path> [--json]";

    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal) { "team", "cwd", "key", "orchestrator-pid" };

    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal) { "journal", "rollup", "closing", "json" };

    /// <summary>
    /// Whether the call is <c>progress list</c>, <c>show</c>, <c>off</c> or <c>on</c>. The owner guard lets all four through: off and on replace
    /// touching or removing a file, which nothing guarded (files-off-disk §6).
    /// </summary>
    internal static bool IsRead(RosterContext c) => c.Command == "progress" && c.Argv.Skip(1).FirstOrDefault() is "list" or "show" or "off" or "on";

    internal static int Run(RosterContext c)
    {
        var words = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        var argv = c.Argv.Skip(1).ToList();
        for (var i = 0; i < argv.Count; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                words.Add(argv[i]);
                continue;
            }

            var key = argv[i][2..];
            if (BoolFlags.Contains(key))
            {
                flags[key] = null;
            }
            else if (ValueFlags.Contains(key) && i + 1 < argv.Count)
            {
                flags[key] = argv[++i];
            }
            else
            {
                throw new RosterFailure($"progress: unknown flag --{key}; {Usage}");
            }
        }

        var progress = c.Services.Hierarchy.Progress;
        var pool = c.ProgressPool;
        return words switch
        {
            ["list"] => List(c, progress, pool, flags.GetValueOrDefault("team")),
            ["show", ..] => Show(c, progress, pool, words.Skip(1).ToList(), flags),
            [var sw and ("off" or "on"), var t] => Out(c, progress.SetOff(pool, Plain(t), sw == "off")),
            _ => throw new RosterFailure(Usage),
        };
    }

    private static int List(RosterContext c, IHierarchyProgress progress, string pool, string? team)
    {
        var teams = new JsonArray();
        foreach (var t in team is null ? progress.DocTeams(pool) : [Plain(team)])
        {
            var docs = new JsonArray();
            IReadOnlyList<ProgressDoc> found;
            try
            {
                found = progress.Docs(pool, t);
            }
            catch (StoreUnavailableException)
            {
                // busy: the team lists nothing rather than its possibly stale files
                found = [];
            }

            foreach (var d in found)
            {
                docs.Add(new JsonObject { ["name"] = d.Name, ["kind"] = d.Kind, ["key"] = d.Key, ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(d.Body), ["updated_ms"] = d.UpdatedMs });
            }

            teams.Add(new JsonObject { ["team"] = t, ["off"] = progress.IsOff(pool, t), ["docs"] = docs });
        }

        c.Out(new JsonObject { ["pool"] = pool, ["teams"] = teams });
        return 0;
    }

    private static int Show(RosterContext c, IHierarchyProgress progress, string pool, List<string> words, Dictionary<string, string?> flags)
    {
        ProgressDoc? doc;
        string asked;
        if (flags.GetValueOrDefault("key") is { } key)
        {
            if (words.Count > 0 || flags.ContainsKey("journal") || flags.ContainsKey("rollup") || flags.ContainsKey("closing"))
            {
                throw new RosterFailure(Usage);
            }

            asked = key;
            doc = progress.DocByKey(key);
        }
        else
        {
            if (words is not [var given, ..] || words.Count > 2)
            {
                throw new RosterFailure(Usage);
            }

            var team = Plain(given);
            var stream = words.Count == 2 ? Plain(words[1]) : null;
            var name = (stream, flags.ContainsKey("journal"), flags.ContainsKey("rollup"), flags.ContainsKey("closing")) switch
            {
                (null, false, _, false) => "rollup.md",
                (null, false, false, true) => "closing.md",
                ({ } s, false, false, false) => s + ".md",
                ({ } s, true, false, false) => s + ".journal.md",
                ({ } s, false, false, true) => s + ".closing.md",
                _ => throw new RosterFailure(Usage),
            };
            asked = team + "/" + name;
            doc = progress.Docs(pool, team).FirstOrDefault(d => d.Name == name);
        }

        if (doc is null)
        {
            throw new RosterFailure($"progress show: no progress document {asked}", exitCode: 1);
        }

        if (flags.ContainsKey("json"))
        {
            var row = new JsonObject { ["key"] = doc.Key, ["team"] = doc.Team, ["name"] = doc.Name, ["kind"] = doc.Kind, ["updated_ms"] = doc.UpdatedMs, ["body"] = doc.Body };
            if (doc.Raw is { } raw)
            {
                // a body that is not valid UTF-8: its exact bytes
                row["body_base64"] = Convert.ToBase64String(raw);
            }

            c.Out(row);
        }
        else
        {
            c.Stdout.Write(doc.Body);
        }

        return 0;
    }

    private static int Out(RosterContext c, JsonValue answer)
    {
        c.Out(answer);
        return 0;
    }

    /// <summary>A team or stream word as a plain name: no path separator, no leading dot.</summary>
    private static string Plain(string word) =>
        word.Length > 0 && !word.StartsWith('.') && !word.Contains('/', StringComparison.Ordinal) && !word.Contains('\\', StringComparison.Ordinal)
            ? word
            : throw new RosterFailure($"progress: \"{word}\" is not a plain name");
}
