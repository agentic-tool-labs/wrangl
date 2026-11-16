using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;

namespace Ah.Cli.Verbs.Qa;

/// <summary>One <c>ah qa</c> call: checks the command line, asks the daemon, and prints its answer.</summary>
internal sealed class QaCommand(ServiceGraph graph, QaOptions opts, string verb, QaOutput output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static readonly string[] Priorities = ["gate", "lane", "adhoc"];

    private static readonly Dictionary<string, string[]> Flags = new(StringComparer.Ordinal)
    {
        ["submit"] = ["repo", "sha", "set", "base", "priority", "fresh", "session", "json", "cwd"],
        ["next"] = ["max-set", "session", "json", "cwd"],
        ["done"] = ["result", "error", "session", "json", "cwd"],
        ["release"] = ["session", "json", "cwd"],
        ["status"] = ["json", "cwd"],
        ["list"] = ["all", "json", "cwd"],
    };

    internal int Run()
    {
        if (!Flags.TryGetValue(verb, out var allowed))
        {
            throw output.Usage($"unknown verb {verb}");
        }

        foreach (var key in opts.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal) && key != "help")
            {
                throw output.Usage($"{verb}: unknown flag --{key}");
            }
        }

        var body = new JsonObject();
        switch (verb)
        {
            case "submit":
                Submit(body);
                break;
            case "next":
                Next(body);
                break;
            case "done":
                Done(body);
                break;
            case "release":
                body["id"] = OneId();
                Hint(body);
                break;
            case "status":
                if (opts.Positional.Count > 1)
                {
                    throw output.Usage("status takes at most one entry id");
                }

                if (opts.Positional.Count == 1)
                {
                    body["id"] = opts.Positional[0];
                }

                break;
            default:
                if (opts.Positional.Count > 0)
                {
                    throw output.Usage("list takes no entry id");
                }

                if (opts.Has("all"))
                {
                    body["all"] = true;
                }

                break;
        }

        var answer = Ask(body);
        if (opts.Has("json"))
        {
            output.Line(JsonText.Stringify(answer, 0));
            return 0;
        }

        Print(answer);
        return 0;
    }

    private string Value(string key)
    {
        if (opts.Text(key) is not { Length: > 0 } value)
        {
            throw output.Usage($"{verb}: --{key} needs a value");
        }

        return value;
    }

    private string OneId()
    {
        if (opts.Positional.Count != 1)
        {
            throw output.Usage($"{verb} needs exactly one entry id");
        }

        return opts.Positional[0];
    }

    private void Hint(JsonObject body)
    {
        if (opts.Has("session"))
        {
            body["session_id"] = Value("session");
        }
    }

    private void Submit(JsonObject body)
    {
        if (opts.Positional.Count > 0)
        {
            throw output.Usage("submit takes no positional words");
        }

        var repo = Value("repo");
        if (!Path.IsPathRooted(repo))
        {
            throw output.Usage("submit: --repo must be an absolute path");
        }

        body["repo"] = repo;
        body["sha"] = Value("sha");
        body["set"] = Value("set");
        if (opts.Has("base"))
        {
            body["base"] = Value("base");
        }

        if (opts.Has("priority"))
        {
            var priority = Value("priority");
            if (!Priorities.Contains(priority, StringComparer.Ordinal))
            {
                throw output.Usage("submit: --priority must be gate, lane or adhoc");
            }

            body["priority"] = priority;
        }

        if (opts.Has("fresh"))
        {
            body["fresh"] = true;
        }

        Hint(body);
    }

    private void Next(JsonObject body)
    {
        if (opts.Positional.Count > 0)
        {
            throw output.Usage("next takes no positional words");
        }

        if (opts.Has("max-set"))
        {
            var maxSet = Value("max-set");
            if (maxSet is not ("full" or "other"))
            {
                throw output.Usage("next: --max-set must be full or other");
            }

            body["max_set"] = maxSet;
        }

        Hint(body);
    }

    private void Done(JsonObject body)
    {
        body["id"] = OneId();
        if (opts.Has("result") == opts.Has("error"))
        {
            throw output.Usage("done needs exactly one of --result and --error");
        }

        if (opts.Has("result"))
        {
            body["result"] = Path.GetFullPath(Value("result"));
        }
        else
        {
            body["error"] = Value("error");
        }

        Hint(body);
    }

    private JsonObject Ask(JsonObject body)
    {
        DaemonReply reply;
        try
        {
            reply = graph.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/qa/" + verb, Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), Budget)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw output.Refused("daemon-unavailable", e.Message);
        }

        if (reply.Status == 0)
        {
            throw output.Refused("daemon-unavailable", reply.FallbackReason ?? "down");
        }

        JsonObject? parsed = null;
        try
        {
            parsed = JsonText.Parse(reply.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : null;
        }
        catch (JsonParseException)
        {
            // an unreadable body is reported below with the status
        }

        if (reply.Status is >= 200 and < 300 && parsed is not null)
        {
            return parsed;
        }

        var error = parsed?.Get("error");
        var code = error?.Member("code") is { IsText: true } c ? c.AsString() : $"http-{reply.Status}";
        var message = error?.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        throw output.Refused(code, message);
    }

    private void Print(JsonObject answer)
    {
        if (answer.Get("entries") is { IsArray: true } entries)
        {
            foreach (var entry in entries.AsArray())
            {
                output.Line(ListLine(entry.AsObject()));
            }

            return;
        }

        if (answer.Get("entry") is not { IsMap: true } job)
        {
            return;
        }

        var e = job.AsObject();
        if (verb is "done" or "release")
        {
            output.Line($"entry {Text(e, "id")} {Text(e, "state")}");
            return;
        }

        output.Line($"entry {Text(e, "id")}");
        if (verb == "submit")
        {
            return;
        }

        output.Line($"repo {Text(e, "repo")}");
        output.Line($"sha {Text(e, "sha")}");
        if (Text(e, "base") is { Length: > 0 } baseSha)
        {
            output.Line($"base {baseSha}");
        }

        output.Line($"set {Text(e, "set")}");
        output.Line($"fresh {(e.Get("fresh") is { IsTruthy: true } ? "true" : "false")}");
        output.Line($"priority {Text(e, "priority")}");
        if (verb != "status")
        {
            return;
        }

        output.Line($"state {Text(e, "state")}");
        output.Line($"requesters {string.Join(",", Requesters(e))}");
        foreach (var (label, key) in new[] { ("claimed_by", "claimed_by"), ("verdict", "verdict"), ("reason", "reason") })
        {
            if (Text(e, key) is { Length: > 0 } value)
            {
                output.Line($"{label} {value}");
            }
        }

        output.Line($"age {Age(e)}");
    }

    private string ListLine(JsonObject e) =>
        $"{Text(e, "id")} {Text(e, "state")} {Text(e, "priority")} {Text(e, "set")} {Text(e, "sha")[..Math.Min(7, Text(e, "sha").Length)]} "
        + $"requesters={Requesters(e).Count} claimer={(Text(e, "claimed_by") is { Length: > 0 } c ? c : "-")} age={Age(e)}";

    private static string Text(JsonObject e, string key) => e.Get(key) is { IsText: true } v ? v.AsString() : string.Empty;

    private static List<string> Requesters(JsonObject e) =>
        e.Get("requesters") is { IsArray: true } list ? [.. list.AsArray().Where(r => r.IsText).Select(r => r.AsString())] : [];

    private string Age(JsonObject e)
    {
        if (e.Get("created") is not { IsNumber: true } created)
        {
            return "-";
        }

        var seconds = Math.Max(0, (long)((graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds() - created.AsNumber()) / 1000));
        return seconds < 60
            ? seconds.ToString(CultureInfo.InvariantCulture) + "s"
            : seconds < 3600 ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m" : (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
    }
}
