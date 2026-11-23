using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah lessons propose|list</c> (ah2-54): lessons from earlier work, a lower-authority store than the rules (adr-30). Writes go to the daemon, which proves the caller;
/// reads run locally. Exit codes: 0 ok, 1 not found, 2 refused or bad input.
/// </summary>
internal static class LessonsVerb
{
    internal const string Usage =
        "usage: ah lessons propose --lesson T --when W --pointer P [--happened H] [--role R] [--path P]... [--until <work id>] [--team T] [--json] [--cwd <dir>] | list [--scope team|role|repo|global] [--role R] [--team T] [--all] [--brief <request id>] [--json] [--cwd <dir>]\n"
        + "       ah lessons promote L<n> --to role|repo|global [--role R] [--json] [--cwd <dir>] | retire L<n> --why W [--json] [--cwd <dir>]   (the user or the top-level Orchestrator)";

    private static readonly string[] NotWithBrief = ["scope", "role", "team", "all"];

    private static readonly HashSet<string> Bools = new(StringComparer.Ordinal) { "json", "all" };

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["propose"] = ["lesson", "when", "pointer", "happened", "role", "path", "until", "team", "json", "cwd"],
        ["list"] = ["scope", "role", "team", "all", "brief", "json", "cwd"],
        ["promote"] = ["to", "role", "json", "cwd"],
        ["retire"] = ["why", "json", "cwd"],
    };

    internal static int Run(CliContext context, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count > 0 && args[0] is "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return Lessons.Ok;
        }

        if (args.Count == 0 || !Allowed.TryGetValue(args[0], out var allowed))
        {
            return Fail(stderr, Usage);
        }

        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        var paths = new List<string>();
        long? id = null;
        var first = 1;
        if (args[0] is "promote" or "retire")
        {
            if (args.Count < 2 || !args[1].StartsWith('L') || !long.TryParse(args[1].AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                return Fail(stderr, $"ah lessons {args[0]}: name the lesson as L<n>; {Usage}");
            }

            id = n;
            first = 2;
        }

        for (var i = first; i < args.Count; i++)
        {
            var name = args[i].StartsWith("--", StringComparison.Ordinal) ? args[i][2..] : null;
            if (name is null || !allowed.Contains(name))
            {
                return Fail(stderr, $"ah lessons {args[0]}: {args[i]} is not understood; {Usage}");
            }

            if (Bools.Contains(name))
            {
                flags[name] = null;
            }
            else if (i + 1 < args.Count)
            {
                if (name == "path")
                {
                    paths.Add(args[++i]);
                }
                else
                {
                    flags[name] = args[++i];
                }
            }
            else
            {
                return Fail(stderr, $"ah lessons {args[0]}: {args[i]} needs a value; {Usage}");
            }
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah lessons: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        var dir = BacklogVerb.DirOf(g, flags.GetValueOrDefault("cwd") ?? Directory.GetCurrentDirectory());
        return args[0] switch
        {
            "propose" => Propose(context, g, dir, flags, paths, stdout, stderr),
            "list" => List(context, g, dir, flags, stdout, stderr),
            "promote" => Change(g, "promote", new JsonObject { ["op"] = "promote", ["dir"] = dir, ["id"] = id!.Value }, flags, ["to", "role"], ["to"], stdout, stderr),
            _ => Change(g, "retire", new JsonObject { ["op"] = "retire", ["dir"] = dir, ["id"] = id!.Value }, flags, ["why"], ["why"], stdout, stderr),
        };
    }

    /// <summary>Promote or retire: the daemon checks who may, and answers.</summary>
    private static int Change(ServiceGraph g, string op, JsonObject body, Dictionary<string, string?> flags, string[] fields, string[] needed, TextWriter stdout, TextWriter stderr)
    {
        if (needed.FirstOrDefault(f => !flags.ContainsKey(f)) is { } missing)
        {
            return Fail(stderr, $"ah lessons {op}: --{missing} is needed; {Usage}");
        }

        foreach (var field in fields)
        {
            if (flags.GetValueOrDefault(field) is { } value)
            {
                body[field] = value;
            }
        }

        return Answer(g, op, body, flags.ContainsKey("json"), stdout, stderr);
    }

    /// <summary>Posts <paramref name="body"/> and prints the daemon's answer; its code is the exit code.</summary>
    private static int Answer(ServiceGraph g, string op, JsonObject body, bool json, TextWriter stdout, TextWriter stderr)
    {
        var reply = g.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/lessons", JsonText.ToUtf8(body, 0), TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
        JsonValue? answer = null;
        if (reply.Status == 200)
        {
            try
            {
                answer = JsonText.Parse(reply.Body.ToArray());
            }
            catch (JsonParseException)
            {
                answer = null;
            }
        }

        if (answer?.Member("code") is not { IsNumber: true } codeValue)
        {
            return Fail(stderr, $"ah lessons {op}: the daemon did not take the write ({(reply.Status == 0 ? reply.FallbackReason : "status " + reply.Status.ToString(System.Globalization.CultureInfo.InvariantCulture))}); nothing was written; start ahd and try again");
        }

        var code = (int)codeValue.AsNumber();
        var message = answer.Value.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (code != Lessons.Ok)
        {
            return Fail(stderr, $"ah lessons {op}: {message}", code);
        }

        stdout.WriteLine(json ? JsonText.Stringify(answer.Value, 0) : message);
        return code;
    }

    private static int Fail(TextWriter stderr, string message, int code = Lessons.Refused)
    {
        stderr.WriteLine(message);
        return code;
    }

    private static int Propose(CliContext context, ServiceGraph g, string dir, Dictionary<string, string?> flags, List<string> paths, TextWriter stdout, TextWriter stderr)
    {
        foreach (var needed in new[] { "lesson", "when", "pointer" })
        {
            if (!flags.ContainsKey(needed))
            {
                return Fail(stderr, $"ah lessons propose: --{needed} is needed; {Usage}");
            }
        }

        if ((flags.GetValueOrDefault("team") ?? RulesStoreVerb.CallerAsk(context.Env, g, dir).Team) is not { Length: > 0 } team)
        {
            return Fail(stderr, $"ah lessons propose: you have no team; name one: --team <name>; {Usage}");
        }

        var pathArray = new JsonArray();
        foreach (var path in paths)
        {
            pathArray.Add(path);
        }

        var body = new JsonObject { ["op"] = "propose", ["dir"] = dir, ["team"] = team, ["paths"] = pathArray };
        foreach (var field in new[] { "lesson", "when", "pointer", "happened", "role", "until" })
        {
            if (flags.GetValueOrDefault(field) is { } value)
            {
                body[field] = value;
            }
        }

        return Answer(g, "propose", body, flags.ContainsKey("json"), stdout, stderr);
    }

    private static int List(CliContext context, ServiceGraph g, string dir, Dictionary<string, string?> flags, TextWriter stdout, TextWriter stderr)
    {
        if (flags.GetValueOrDefault("scope") is { } scope && !Lessons.Scopes.Contains(scope))
        {
            return Fail(stderr, $"ah lessons list: --scope is one of {string.Join(", ", Lessons.Scopes)}; {Usage}");
        }

        if (Lessons.Read(context.Env.AhHome, dir, out var error) is not { } stored)
        {
            return Fail(stderr, $"ah lessons list: the lessons could not be read: {error}");
        }

        List<LessonRow> shown;
        if (flags.GetValueOrDefault("brief") is { } brief)
        {
            if (NotWithBrief.FirstOrDefault(flags.ContainsKey) is { } other)
            {
                return Fail(stderr, $"ah lessons list: --brief shows exactly what that request matches; --{other} does not go with it; {Usage}");
            }

            if (g.Hierarchy.Core.ResolveMsgId(brief, "request", dir, null, null).Key is not { } key)
            {
                return Fail(stderr, $"ah lessons list: no request {brief}", Lessons.NotFound);
            }

            var read = g.Hierarchy.Core.ReadMsgFile(key);
            try
            {
                shown = [.. LessonsFooter.ForRequest(g, context.Env.AhHome, dir, read.Member("fm"), read.Member("text") is { IsText: true } t ? t.AsString() : string.Empty)];
            }
            catch (InvalidOperationException e)
            {
                return Fail(stderr, $"ah lessons list: the lessons could not be read: {e.Message}");
            }
        }
        else
        {
            var all = flags.ContainsKey("all");
            shown = [.. stored.Select(r => Lessons.AsRead(g, context.Env.AhHome, dir, r)).Where(r => (all || r.State == "open")
                && (flags.GetValueOrDefault("scope") is not { } s || r.Scope == s)
                && (flags.GetValueOrDefault("role") is not { } role || r.Role == role)
                && (flags.GetValueOrDefault("team") is not { } team || r.Team == team))];
        }
        if (flags.ContainsKey("json"))
        {
            var array = new JsonArray();
            foreach (var row in shown)
            {
                array.Add(Lessons.Json(row));
            }

            stdout.WriteLine(JsonText.Stringify(new JsonObject { ["lessons"] = array }, 0));
            return Lessons.Ok;
        }

        foreach (var row in shown)
        {
            stdout.WriteLine(Line(row));
        }

        return Lessons.Ok;
    }

    /// <summary>One lesson as <c>list</c> prints it, raw as rule text is: source first, then the lesson and when it applies.</summary>
    private static string Line(LessonRow row)
    {
        var where = row.Scope == Lessons.TeamScope ? $"team {row.Team}" : row.Scope;
        var role = row.Role is { } r ? $", for {r}" : string.Empty;
        var paths = row.Paths.Count > 0 ? $"; paths: {string.Join(", ", row.Paths)}" : string.Empty;
        var happened = row.Happened is { } h ? $"; happened: {h}" : string.Empty;
        var retired = row.State == "open" ? string.Empty : $" [{row.State}{(row.RetiredBy is { } by ? " by " + by : string.Empty)}{(row.RetiredWhy is { } why ? ": " + why : string.Empty)}]";
        return $"L{row.Id} {where}{role}, by {row.By}, source {row.Pointer}: {row.Lesson} (when: {row.Applies}{paths}{happened}){retired}";
    }
}
