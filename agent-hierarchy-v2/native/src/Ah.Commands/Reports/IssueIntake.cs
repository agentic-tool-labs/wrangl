using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Commands;

/// <summary>What issue intake reads and runs: git and <c>gh</c>, the clock, the conventions loader, the snapshot directory and the error log.</summary>
public sealed record IssueIntakeServices(
    IProcessRunner Runner,
    IRawFileWriter Writer,
    IClock Clock,
    IConventions Conventions,
    IHookErrorLog ErrorLog);

/// <summary>
/// <c>ah issue-intake --cwd &lt;abs&gt; --out-dir &lt;abs&gt; (--issues &lt;ref,...&gt; | --labelled)</c>: decides which GitHub issues are
/// eligible pipeline input from their metadata alone, and writes a sanitized snapshot of each eligible one. All GitHub I/O goes
/// through <c>gh</c>, whose output never reaches standard output.
/// </summary>
internal static class IssueIntake
{
    private const string Usage =
        "usage: ah issue-intake --cwd <abs> --out-dir <abs> (--issues <ref,ref,...> | --labelled)\n" +
        "  <ref>       N, #N or https://github.com/<o>/<r>/issues/N; given order kept, duplicates dropped\n" +
        "  --labelled  every open issue carrying issues.trigger_label, in ascending number\n" +
        "Prints one JSON object {repo, trigger_label, trusted_actors, items}, exit 0;\n" +
        "a run-level failure prints {\"error\": \"<code>: <detail>\"}, exit 1.\n";

    private const string OwnerQuery =
        "query IntakeOwner($owner: String!, $name: String!) {\n" +
        "  repository(owner: $owner, name: $name) { owner { __typename login } }\n" +
        "}";

    private const string LabelledQuery =
        "query IntakeLabelled($owner: String!, $name: String!, $label: String!, $after: String) {\n" +
        "  repository(owner: $owner, name: $name) {\n" +
        "    issues(first: 100, after: $after, states: OPEN, labels: [$label]) {\n" +
        "      nodes { number }\n" +
        "      pageInfo { hasNextPage endCursor }\n" +
        "    }\n" +
        "  }\n" +
        "}";

    private const string IssueMetadata =
        "\n" +
        "      state\n" +
        "      lastEditedAt\n" +
        "      labels(first: 100) { nodes { name } }\n" +
        "      timelineItems(last: 100, itemTypes: [LABELED_EVENT, RENAMED_TITLE_EVENT]) {\n" +
        "        nodes {\n" +
        "          __typename\n" +
        "          ... on LabeledEvent { createdAt actor { login } label { name } }\n" +
        "          ... on RenamedTitleEvent { createdAt }\n" +
        "        }\n" +
        "      }";

    private const string MetaQuery =
        "query IntakeMeta($owner: String!, $name: String!, $number: Int!) {\n" +
        "  repository(owner: $owner, name: $name) {\n" +
        "    issue(number: $number) {" + IssueMetadata + "\n" +
        "      comments(last: 100) { nodes { id author { login } createdAt lastEditedAt } }\n" +
        "    }\n" +
        "  }\n" +
        "}";

    private const string FetchQuery =
        "query IntakeFetch($owner: String!, $name: String!, $number: Int!, $ids: [ID!]!) {\n" +
        "  repository(owner: $owner, name: $name) {\n" +
        "    issue(number: $number) {\n" +
        "      title\n" +
        "      bodyHTML" + IssueMetadata + "\n" +
        "    }\n" +
        "  }\n" +
        "  nodes(ids: $ids) { ... on IssueComment { id bodyHTML lastEditedAt } }\n" +
        "}";

    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static readonly HashSet<string> DroppedElements = new(StringComparer.Ordinal)
    {
        "template", "noscript", "script", "style", "rp", "video", "audio", "object", "canvas",
        "iframe", "noembed", "noframes", "datalist", "dialog", "title",
    };

    private static readonly HashSet<string> LineElements = new(StringComparer.Ordinal) { "div", "li", "tr", "dt", "dd" };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "pre", "blockquote", "ul", "ol", "table", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
    };

    /// <summary>A run-level failure: <c>Detail</c> is printed; <c>Diag</c>, when present, goes only to the hook error log.</summary>
    private sealed class Fail(string code, string detail, string? diag = null) : Exception($"{code}: {detail}")
    {
        public string? Diag { get; } = diag;
    }

    private sealed record GhRun(int? Status, string Out, string Err);

    private sealed record Graph(JsonValue? Data, int? Status, string Err);

    private sealed record Context(
        IssueIntakeServices Services,
        string Owner,
        string Name,
        string Repo,
        string Label,
        string OutDir,
        HashSet<string> Trusted);

    private readonly record struct Ref(double Number, bool Other);

    private sealed record Verdict(string? Reason, string? Snapshot, string? Sha256, int Lines);

    public static int Run(IssueIntakeServices s, IReadOnlyList<string> args, TextWriter stdout)
    {
        try
        {
            return Main(s, args, stdout);
        }
        catch (Fail fail)
        {
            return Failed(s, stdout, fail);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failed(s, stdout, new Fail("intake-failed", "unexpected error", e.Message));
        }
    }

    private static int Failed(IssueIntakeServices s, TextWriter stdout, Fail fail)
    {
        if (fail.Diag is not null)
        {
            s.ErrorLog.Log(new HookErrorRecord("issue-intake", fail.Diag));
        }

        stdout.Write(JsonText.Stringify(new JsonObject { ["error"] = fail.Message }, 0) + "\n");
        return 1;
    }

    private static int Main(IssueIntakeServices s, IReadOnlyList<string> argv, TextWriter stdout)
    {
        var opts = ParseArgs(argv);
        if (opts.Help)
        {
            stdout.Write(Usage);
            return 0;
        }

        if (string.IsNullOrEmpty(opts.Cwd) || string.IsNullOrEmpty(opts.OutDir))
        {
            throw new Fail("usage", "--cwd and --out-dir are required");
        }

        var hasIssues = !string.IsNullOrEmpty(opts.Issues);
        if (hasIssues == opts.Labelled)
        {
            throw new Fail("usage", "give exactly one of --issues and --labelled");
        }

        var git = s.Conventions;
        var origin = git.TryGit(opts.Cwd, ["remote", "get-url", "origin"]) is { } o ? JsText.Trim(o) : null;
        if (origin is null || ParseGithubRemote(origin) is not { } remote)
        {
            throw new Fail("origin-not-github", string.IsNullOrEmpty(origin) ? "no origin remote" : origin);
        }

        var (owner, name) = remote;
        var repo = $"{owner}/{name}";
        var refs = hasIssues ? ParseRefs(opts.Issues!, repo) : null;

        var conv = git.LoadConventions(opts.Cwd);
        var conventionsPath = conv.Path;
        if (conv.Status == "absent")
        {
            throw new Fail("conventions-absent", $"no {conventionsPath} on origin's default branch");
        }

        if (conv.Conventions is null)
        {
            throw new Fail("conventions-invalid", conv.Status.StartsWith("invalid: ", StringComparison.Ordinal) ? conv.Status["invalid: ".Length..] : conv.Status);
        }

        var issues = conv.Conventions.Get("issues");
        var labelValue = issues?.Member("trigger_label");
        if (labelValue is not { IsTruthy: true } labelTruthy)
        {
            throw new Fail("no-trigger-label", $"set issues.trigger_label in {conventionsPath}");
        }

        var label = JsConvert.ToJsString(labelTruthy);
        if (Gh(s, ["auth", "status", "--hostname", "github.com"], null).Status != 0)
        {
            throw new Fail("gh-unavailable", "`gh auth status --hostname github.com` failed");
        }

        JsonArray trusted;
        if (issues?.Member("trusted_actors") is { IsArray: true } listed)
        {
            trusted = listed.AsArray();
        }
        else
        {
            trusted = OwnerActors(s, owner, name, conventionsPath);
        }

        var trustedLower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in trusted)
        {
            trustedLower.Add(JsLower(JsConvert.ToJsString(actor)));
        }

        var ctx = new Context(s, owner, name, repo, label, opts.OutDir, trustedLower);
        var targets = refs ?? LabelledNumbers(s, owner, name, label).Select(n => new Ref(n, false)).ToList();
        var items = new JsonArray();
        foreach (var target in targets)
        {
            items.Add(Item(ctx, target));
        }

        stdout.Write(
            JsonText.Stringify(
                new JsonObject { ["repo"] = repo, ["trigger_label"] = label, ["trusted_actors"] = trusted, ["items"] = items },
                0) + "\n");
        return 0;
    }

    // ---------------------------------------------------------------- arguments

    private sealed record Options(string? Cwd, string? OutDir, string? Issues, bool Labelled, bool Help);

    private sealed record ArgToken(string? Name, string RawName, string? Value, bool InlineValue, string? Positional);

    /// <summary>
    /// <c>util.parseArgs</c> in strict mode over <c>--cwd</c>, <c>--out-dir</c>, <c>--issues</c> (strings), <c>--labelled</c> and
    /// <c>--help</c>/<c>-h</c> (booleans), positionals refused; the first problem in argument order is the one reported.
    /// </summary>
    private static Options ParseArgs(IReadOnlyList<string> args)
    {
        var tokens = new List<ArgToken>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (var k = i + 1; k < args.Count; k++)
                {
                    tokens.Add(new ArgToken(null, string.Empty, null, false, args[k]));
                }

                break;
            }

            if (arg.Length == 2 && arg[0] == '-' && arg[1] != '-')
            {
                var name = LongNameOfShort(arg[1]);
                string? value = null;
                if (IsStringOption(name) && i + 1 < args.Count)
                {
                    value = args[++i];
                }

                tokens.Add(new ArgToken(name, arg, value, false, null));
            }
            else if (arg.Length > 2 && arg[0] == '-' && arg[1] != '-')
            {
                for (var j = 1; j < arg.Length; j++)
                {
                    var name = LongNameOfShort(arg[j]);
                    if (IsStringOption(name))
                    {
                        tokens.Add(new ArgToken(name, "-" + arg[j], arg[(j + 1)..], true, null));
                        break;
                    }

                    tokens.Add(new ArgToken(name, "-" + arg[j], null, false, null));
                }
            }
            else if (arg.Length > 2 && arg.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=', StringComparison.Ordinal);
                if (equals > 2)
                {
                    var name = arg[2..equals];
                    tokens.Add(new ArgToken(name, "--" + name, arg[(equals + 1)..], true, null));
                }
                else
                {
                    var name = arg[2..];
                    string? value = null;
                    if (IsStringOption(name) && i + 1 < args.Count)
                    {
                        value = args[++i];
                    }

                    tokens.Add(new ArgToken(name, arg, value, false, null));
                }
            }
            else
            {
                tokens.Add(new ArgToken(null, string.Empty, null, false, arg));
            }
        }

        string? cwd = null;
        string? outDir = null;
        string? issues = null;
        var labelled = false;
        var help = false;
        foreach (var token in tokens)
        {
            if (token.Positional is { } positional)
            {
                throw new Fail("usage", $"Unexpected argument '{positional}'. This command does not take positional arguments");
            }

            var name = token.Name!;
            if (!(IsStringOption(name) || name is "labelled" or "help"))
            {
                throw new Fail("usage", $"Unknown option '{token.RawName}'");
            }

            if (IsStringOption(name))
            {
                if (token.Value is null)
                {
                    throw new Fail("usage", $"Option '--{name} <value>' argument missing");
                }

                if (!token.InlineValue && token.Value.StartsWith('-') && token.Value.Length > 1)
                {
                    throw new Fail(
                        "usage",
                        $"Option '--{name}' argument is ambiguous.\nDid you forget to specify the option argument for '--{name}'?\nTo specify an option argument starting with a dash use '--{name}=-XYZ'.");
                }

                switch (name)
                {
                    case "cwd":
                        cwd = token.Value;
                        break;
                    case "out-dir":
                        outDir = token.Value;
                        break;
                    default:
                        issues = token.Value;
                        break;
                }
            }
            else
            {
                if (token.Value is not null)
                {
                    var shown = name == "help" ? "-h, --help" : "--labelled";
                    throw new Fail("usage", $"Option '{shown}' does not take an argument");
                }

                if (name == "help")
                {
                    help = true;
                }
                else
                {
                    labelled = true;
                }
            }
        }

        return new Options(cwd, outDir, issues, labelled, help);
    }

    private static string LongNameOfShort(char c) => c == 'h' ? "help" : c.ToString();

    private static bool IsStringOption(string name) => name is "cwd" or "out-dir" or "issues";

    // ---------------------------------------------------------------- remote and refs

    private static bool StartsWithAscii(string s, int at, string prefix) =>
        at + prefix.Length <= s.Length && string.Compare(s, at, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0
        && s.AsSpan(at, prefix.Length).ToString().All(static c => c < 128);

    /// <summary>
    /// <c>https://[user@]github.com/o/r[.git][/]</c>, <c>git@github.com:o/r\u2026</c> or <c>ssh://git@github.com/o/r\u2026</c> with the
    /// letters in any case: the owner and repo, or null.
    /// </summary>
    private static (string Owner, string Name)? ParseGithubRemote(string url)
    {
        var rest = default(string);
        if (StartsWithAscii(url, 0, "https://"))
        {
            var after = 8;
            if (StartsWithAscii(url, after, "github.com/"))
            {
                rest = url[(after + 11)..];
            }
            else
            {
                var at = url.IndexOf('@', after);
                var slash = url.IndexOf('/', after);
                if (at > after && (slash < 0 || slash > at) && StartsWithAscii(url, at + 1, "github.com/"))
                {
                    rest = url[(at + 1 + 11)..];
                }
            }
        }
        else if (StartsWithAscii(url, 0, "git@github.com:"))
        {
            rest = url["git@github.com:".Length..];
        }
        else if (StartsWithAscii(url, 0, "ssh://git@github.com/"))
        {
            rest = url["ssh://git@github.com/".Length..];
        }

        if (rest is null)
        {
            return null;
        }

        var cut = rest.IndexOf('/', StringComparison.Ordinal);
        if (cut <= 0)
        {
            return null;
        }

        var owner = rest[..cut];
        var tail = rest[(cut + 1)..];
        if (tail.EndsWith('/'))
        {
            tail = tail[..^1];
        }

        if (tail.Length == 0 || tail.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var name = tail;
        if (tail.Length > 4 && tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = tail[..^4];
        }

        return (owner, name);
    }

    private static List<Ref> ParseRefs(string list, string repo)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<Ref>();
        foreach (var raw in list.Split(','))
        {
            var text = JsText.Trim(raw);
            if (text.Length == 0)
            {
                continue;
            }

            string? key = null;
            Ref item;
            var digits = text.StartsWith('#') ? text[1..] : text;
            if (digits.Length > 0 && digits.All(static c => c is >= '0' and <= '9'))
            {
                item = new Ref(double.Parse(digits, CultureInfo.InvariantCulture), false);
            }
            else if (ParseIssueUrl(text) is { } url)
            {
                var refRepo = (url.Owner + "/" + url.Repo).ToLowerInvariant();
                item = new Ref(url.Number, !string.Equals(refRepo, repo, StringComparison.OrdinalIgnoreCase));
                if (item.Other)
                {
                    key = $"{refRepo}#{JsConvert.NumberToString(item.Number)}";
                }
            }
            else
            {
                throw new Fail("usage", $"not an issue ref: {text}");
            }

            key ??= $"#{JsConvert.NumberToString(item.Number)}";
            if (seen.Add(key))
            {
                refs.Add(item);
            }
        }

        return refs;
    }

    private static (string Owner, string Repo, double Number)? ParseIssueUrl(string text)
    {
        if (!StartsWithAscii(text, 0, "https://github.com/"))
        {
            return null;
        }

        var parts = text["https://github.com/".Length..];
        var first = parts.IndexOf('/', StringComparison.Ordinal);
        if (first <= 0)
        {
            return null;
        }

        var owner = parts[..first];
        var afterOwner = parts[(first + 1)..];
        var second = afterOwner.IndexOf('/', StringComparison.Ordinal);
        if (second <= 0)
        {
            return null;
        }

        var repo = afterOwner[..second];
        var tail = afterOwner[(second + 1)..];
        if (!StartsWithAscii(tail, 0, "issues/"))
        {
            return null;
        }

        var number = tail["issues/".Length..];
        if (number.EndsWith('/'))
        {
            number = number[..^1];
        }

        if (number.Length == 0 || !number.All(static c => c is >= '0' and <= '9'))
        {
            return null;
        }

        return (owner, repo, double.Parse(number, CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- gh

    private static GhRun Gh(IssueIntakeServices s, IReadOnlyList<string> args, string? input)
    {
        var argv = new List<string> { "gh" };
        argv.AddRange(args);
        var request = new ProcessRequest(argv, null, null, input is null ? default : Utf8Text.Encode(input), null, 64 << 20);
        var result = s.Runner.Run(request);
        if (result is { ExitCode: 127, Signal: null, TimedOut: false } && result.Stdout.IsEmpty && result.Stderr.IsEmpty)
        {
            throw new Fail("gh-unavailable", "gh is not installed");
        }

        var stdout = Utf8Text.Decode(result.Stdout.Span);
        if (result is { ExitCode: 0, TimedOut: false, Truncated: false })
        {
            return new GhRun(0, stdout, string.Empty);
        }

        return new GhRun(result.TimedOut || result.Truncated ? null : result.ExitCode, stdout, Utf8Text.Decode(result.Stderr.Span));
    }

    /// <summary>
    /// One GraphQL request. <c>gh</c> exits non-zero whenever the response carries errors while still printing the body, so
    /// callers judge by the data.
    /// </summary>
    private static Graph GraphQl(IssueIntakeServices s, string query, JsonObject variables)
    {
        var body = JsonText.Stringify(new JsonObject { ["query"] = query, ["variables"] = variables }, 0);
        var r = Gh(s, ["api", "graphql", "--input", "-"], body);
        JsonValue? data = null;
        try
        {
            if (JsonText.Parse(r.Out).Member("data") is { IsNull: false } d)
            {
                data = d;
            }
        }
        catch (Exception e) when (e is JsonParseException)
        {
            // an answer that is not JSON has no data
        }

        return new Graph(data, r.Status, r.Err);
    }

    private static string FirstLine(string text) => JsText.Trim(text).Split('\n')[0];

    private static Fail GhFailed(string detail, Graph r)
    {
        var line = FirstLine(r.Err);
        if (line.Length > 200)
        {
            line = line[..200];
        }

        var status = r.Status is { } code ? code.ToString(CultureInfo.InvariantCulture) : "null";
        return new Fail("intake-failed", detail, $"{detail}: gh exit {status}: {line}");
    }

    private static bool ContainsAsciiInsensitive(string text, string needle)
    {
        for (var i = 0; i + needle.Length <= text.Length; i++)
        {
            var match = true;
            for (var k = 0; k < needle.Length; k++)
            {
                var c = text[i + k];
                if (c >= 128 || char.ToLowerInvariant(c) != needle[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The issue in a GraphQL answer; null when GitHub says it does not exist (a present repository with a null issue, or, with no
    /// parseable answer, gh's "Could not resolve to an Issue"). Anything else, a null repository included, fails intake.
    /// </summary>
    private static JsonValue? AnsweredIssue(Graph r, string detail)
    {
        var repository = r.Data?.Member("repository");
        if (repository?.Member("issue") is { IsTruthy: true } issue)
        {
            return issue;
        }

        if (repository is { IsTruthy: true } found)
        {
            if (found.Member("issue") is { IsNull: true })
            {
                return null;
            }
        }
        else if (r.Data is not { IsTruthy: true }
            && (ContainsAsciiInsensitive(r.Err, "could not resolve to an issue") || ContainsAsciiInsensitive(r.Err, "could not resolve to a issue")))
        {
            return null;
        }

        throw GhFailed(detail, r);
    }

    /// <summary>Every connection is present and holds no null node; a partial GraphQL error leaves one missing.</summary>
    private static bool Complete(params JsonValue?[] connections) =>
        connections.All(static c => c?.Member("nodes") is { IsArray: true } nodes && nodes.AsArray().All(static n => n.IsTruthy));

    private static JsonArray OwnerActors(IssueIntakeServices s, string owner, string name, string conventionsPath)
    {
        var r = GraphQl(s, OwnerQuery, new JsonObject { ["owner"] = owner, ["name"] = name });
        var own = r.Data?.Member("repository")?.Member("owner");
        if (own is not { IsTruthy: true } o)
        {
            throw GhFailed("reading the repo owner failed", r);
        }

        if (!(o.Member("__typename") is { IsText: true } kind && kind.AsString() == "User"))
        {
            throw new Fail("org-needs-trusted-actors", $"{JsConvert.ToJsString(o.Member("login"))} is an organization; set issues.trusted_actors in {conventionsPath}");
        }

        return [o.Member("login") ?? JsonValue.Null];
    }

    private static List<double> LabelledNumbers(IssueIntakeServices s, string owner, string name, string label)
    {
        var numbers = new List<double>();
        JsonValue after = JsonValue.Null;
        do
        {
            var r = GraphQl(
                s,
                LabelledQuery,
                new JsonObject { ["owner"] = owner, ["name"] = name, ["label"] = label, ["after"] = after });
            var conn = r.Data?.Member("repository")?.Member("issues");
            if (!Complete(conn))
            {
                throw GhFailed("listing labelled issues failed", r);
            }

            foreach (var node in conn!.Value.Member("nodes")!.Value.AsArray())
            {
                numbers.Add(JsConvert.ToNumber(node.Member("number")));
            }

            if (conn.Value.Member("pageInfo") is not { IsMap: true } page)
            {
                var what = conn.Value.Member("pageInfo") is { IsNull: true } ? "null" : "undefined";
                throw new InvalidOperationException($"Cannot read properties of {what} (reading 'hasNextPage')");
            }

            after = page.Member("hasNextPage") is { IsTruthy: true } ? page.Member("endCursor") ?? JsonValue.Null : JsonValue.Null;
            if (after.IsNull)
            {
                break;
            }
        }
        while (after.IsTruthy);

        return numbers.OrderBy(static n => n).ToList();
    }

    // ---------------------------------------------------------------- the gate

    private static double Time(JsonValue? iso) =>
        iso is { } v && Timestamps.TryParseMs(JsConvert.ToJsString(v), out var ms) ? ms : double.NaN;

    private static string JsLower(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c == '\u212A' ? 'k' : c);
        }

        return sb.ToString();
    }

    private static bool Trusts(Context ctx, JsonValue? login) =>
        login is { IsTruthy: true } l && ctx.Trusted.Contains(JsLower(JsConvert.ToJsString(l)));

    private sealed record Gated(string? Reason, JsonValue Labelled, double At);

    /// <summary>The eligibility checks over one read of an issue's metadata: the first that fails, else the latest trigger-label event.</summary>
    private static Gated Gate(JsonValue issue, string label, Func<JsonValue?, bool> trusts)
    {
        if (!(issue.Member("state") is { IsText: true } state && state.AsString() == "OPEN"))
        {
            return new Gated("closed", JsonValue.Null, 0);
        }

        var labelNodes = issue.Member("labels")?.Member("nodes");
        var hasLabel = labelNodes is { IsArray: true } ln
            && ln.AsArray().Any(l => l.Member("name") is { IsText: true } n && n.AsString() == label);
        if (!hasLabel)
        {
            return new Gated("no-trigger-label", JsonValue.Null, 0);
        }

        var events = issue.Member("timelineItems")?.Member("nodes")!.Value.AsArray().ToList() ?? [];
        JsonValue? labelled = null;
        foreach (var e in events)
        {
            if (e.Member("__typename") is { IsText: true } t && t.AsString() == "LabeledEvent"
                && e.Member("label")?.Member("name") is { IsText: true } name && name.AsString() == label)
            {
                if (labelled is null || Time(e.Member("createdAt")) >= Time(labelled.Value.Member("createdAt")))
                {
                    labelled = e;
                }
            }
        }

        if (labelled is null || !trusts(labelled.Value.Member("actor")?.Member("login")))
        {
            return new Gated("untrusted-labeler", JsonValue.Null, 0);
        }

        var at = Time(labelled.Value.Member("createdAt"));
        var renamedAfter = events.Any(e =>
            e.Member("__typename") is { IsText: true } t && t.AsString() == "RenamedTitleEvent" && Time(e.Member("createdAt")) > at);
        var edited = issue.Member("lastEditedAt") is { IsTruthy: true } last && Time(last) > at;
        if (edited || renamedAfter)
        {
            return new Gated("edited-after-label", JsonValue.Null, 0);
        }

        return new Gated(null, labelled.Value, at);
    }

    // ---------------------------------------------------------------- one issue

    private static string SnapshotPath(string outDir, double number) =>
        NodePath.Join(outDir, $"issue-{JsConvert.NumberToString(number)}.md");

    private static Verdict IntakeIssue(Context ctx, double number)
    {
        var s = ctx.Services;
        var numberText = JsConvert.NumberToString(number);
        var readFailed = $"reading issue #{numberText} failed";
        var meta = GraphQl(s, MetaQuery, new JsonObject { ["owner"] = ctx.Owner, ["name"] = ctx.Name, ["number"] = number });
        var issue = AnsweredIssue(meta, readFailed);
        if (issue is not { } found)
        {
            return new Verdict("not-found", null, null, 0);
        }

        if (!Complete(found.Member("labels"), found.Member("timelineItems"), found.Member("comments")))
        {
            throw GhFailed(readFailed, meta);
        }

        var gated = Gate(found, ctx.Label, login => Trusts(ctx, login));
        if (gated.Reason is not null)
        {
            return new Verdict(gated.Reason, null, null, 0);
        }

        var labelled = gated.Labelled;
        var at = gated.At;
        var included = new List<JsonValue>();
        foreach (var c in found.Member("comments")!.Value.Member("nodes")!.Value.AsArray())
        {
            var edited = c.Member("lastEditedAt");
            if (Trusts(ctx, c.Member("author")?.Member("login")) && (edited is not { IsTruthy: true } || Time(edited) <= at))
            {
                included.Add(c);
            }
        }

        var fetchFailed = $"fetching issue #{numberText} failed";
        var ids = new JsonArray();
        foreach (var c in included)
        {
            ids.Add(c.Member("id") ?? JsonValue.Null);
        }

        var fetch = GraphQl(
            s,
            FetchQuery,
            new JsonObject { ["owner"] = ctx.Owner, ["name"] = ctx.Name, ["number"] = number, ["ids"] = ids });
        var text = AnsweredIssue(fetch, fetchFailed);
        if (text is not { } textIssue)
        {
            return new Verdict("edited-during-intake", null, null, 0);
        }

        if (!Complete(textIssue.Member("labels"), textIssue.Member("timelineItems")) || fetch.Data?.Member("nodes") is not { IsArray: true } fetchedNodes)
        {
            throw GhFailed(fetchFailed, fetch);
        }

        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var n in fetchedNodes.AsArray())
        {
            if (n.IsTruthy)
            {
                byId[JsConvert.ToJsString(n.Member("id"))] = n;
            }
        }

        var changed = !SameValue(textIssue.Member("lastEditedAt"), found.Member("lastEditedAt"))
            || included.Any(c => !byId.TryGetValue(JsConvert.ToJsString(c.Member("id")), out var n) || !SameValue(n.Member("lastEditedAt"), c.Member("lastEditedAt")))
            || Gate(textIssue, ctx.Label, login => Trusts(ctx, login)).Reason is not null;
        if (changed)
        {
            return new Verdict("edited-during-intake", null, null, 0);
        }

        var title = textIssue.Member("title") is { IsNull: false } t ? JsConvert.ToJsString(t) : string.Empty;
        var htmls = new List<string> { textIssue.Member("bodyHTML") is { IsNull: false } b ? JsConvert.ToJsString(b) : string.Empty };
        foreach (var c in included)
        {
            var html = byId[JsConvert.ToJsString(c.Member("id"))].Member("bodyHTML");
            htmls.Add(html is { IsNull: false } h ? JsConvert.ToJsString(h) : string.Empty);
        }

        var rendered = htmls.Select(VisibleText).ToList();
        if (HiddenChars(title) || rendered.Any(r => r.Collapsed || HiddenChars(r.Text)))
        {
            return new Verdict("hidden-content", null, null, 0);
        }

        var contents = rendered.Select(r => ContentBreaks(r.Text)).ToList();
        var snapshot = new StringBuilder();
        var login0 = JsConvert.ToJsString(labelled.Member("actor")?.Member("login"));
        snapshot.Append("UNTRUSTED ISSUE SNAPSHOT ").Append(ctx.Repo).Append('#').Append(numberText)
            .Append(" labelled-by=").Append(login0)
            .Append(" at=").Append(JsConvert.ToJsString(labelled.Member("createdAt")))
            .Append(" fetched=").Append(Timestamps.ToIsoString(s.Clock.UtcNow)).Append('\n')
            .Append("\nTITLE: ").Append(TitleBreaks(title)).Append("\n\nBODY:\n").Append(Gutter(contents[0]));
        for (var i = 0; i < included.Count; i++)
        {
            var c = included[i];
            snapshot.Append("COMMENT ").Append(JsConvert.ToJsString(c.Member("author")?.Member("login")))
                .Append(' ').Append(JsConvert.ToJsString(c.Member("createdAt"))).Append(":\n")
                .Append(Gutter(contents[i + 1]));
        }

        var shot = snapshot.ToString();
        var path = SnapshotPath(ctx.OutDir, number);
        try
        {
            s.Writer.Mkdir(ctx.OutDir);
            s.Writer.WriteAtomic(path, $"{path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp", Utf8Text.Encode(shot));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Fail("intake-failed", $"writing snapshot for #{numberText} failed", e.Message);
        }

        var lines = shot.Count(static c => c == '\n');
        return new Verdict(null, path, Convert.ToHexString(SHA256.HashData(Utf8Text.Encode(shot))).ToLowerInvariant(), lines);
    }

    private static bool SameValue(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var x = a.Value;
        var y = b.Value;
        if (x.Kind != y.Kind)
        {
            return false;
        }

        return x.Kind switch
        {
            JsonKind.Null => true,
            JsonKind.Bool => x.AsBool() == y.AsBool(),
            JsonKind.Number => x.AsNumber() == y.AsNumber(),
            JsonKind.Text => string.Equals(x.AsString(), y.AsString(), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static JsonObject Item(Context ctx, Ref target)
    {
        var issue = target.Number;
        if (target.Other)
        {
            return new JsonObject { ["issue"] = issue, ["eligible"] = false, ["reason"] = "other-repo" };
        }

        Verdict v;
        try
        {
            v = IntakeIssue(ctx, issue);
        }
        catch (Fail)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new Fail("intake-failed", $"processing issue #{JsConvert.NumberToString(issue)} failed", e.Message);
        }

        if (v.Reason is null)
        {
            return new JsonObject
            {
                ["issue"] = issue,
                ["eligible"] = true,
                ["snapshot"] = v.Snapshot!,
                ["sha256"] = v.Sha256!,
                ["lines"] = v.Lines,
            };
        }

        try
        {
            _ = ctx.Services.Writer.Unlink(SnapshotPath(ctx.OutDir, issue));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new Fail("intake-failed", $"removing stale snapshot for #{JsConvert.NumberToString(issue)} failed", e.Message);
        }

        return new JsonObject { ["issue"] = issue, ["eligible"] = false, ["reason"] = v.Reason };
    }

    // ---------------------------------------------------------------- rendered HTML to visible text

    private static bool IsHiddenCodePoint(int cp) =>
        cp == 0x061C || cp is >= 0x200B and <= 0x200F || cp is >= 0x2028 and <= 0x202E || cp is >= 0x2060 and <= 0x206F
        || cp == 0xFEFF || cp is >= 0xE0000 and <= 0xE007F || cp is >= 0xE0100 and <= 0xE01EF;

    /// <summary>A zero-width joiner between two pictographs (the first optionally followed by VS16 or a skin-tone modifier) only joins an emoji.</summary>
    private static string WithoutEmojiJoiners(string text)
    {
        var points = JsText.CodePoints(text).ToList();
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < points.Count; i++)
        {
            var cp = points[i];
            var joins = false;
            if (cp == 0x200D && i + 1 < points.Count && ExtendedPictographic.Contains(points[i + 1]) && i >= 1)
            {
                var before = points[i - 1];
                joins = ExtendedPictographic.Contains(before)
                    || ((before == 0xFE0F || before is >= 0x1F3FB and <= 0x1F3FF) && i >= 2 && ExtendedPictographic.Contains(points[i - 2]));
            }

            if (!joins)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
        }

        return sb.ToString();
    }

    private static bool HiddenChars(string text) => JsText.CodePoints(WithoutEmojiJoiners(text)).Any(IsHiddenCodePoint);

    /// <summary>In a title each line break a consumer might split on becomes a space.</summary>
    private static string TitleBreaks(string text) => ReplaceBreaks(text, " ", includeLineFeed: true);

    /// <summary>In content each such break other than LF becomes an LF.</summary>
    private static string ContentBreaks(string text) => ReplaceBreaks(text, "\n", includeLineFeed: false);

    private static string ReplaceBreaks(string text, string with, bool includeLineFeed)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append(with);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if ((c == '\n' && includeLineFeed) || c is '\u000B' or '\u000C' or '\u0085')
            {
                sb.Append(with);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>Content lines behind a <c>| </c> gutter (a bare <c>|</c> when empty), so none can pose as a structural line.</summary>
    private static string Gutter(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var body = text.EndsWith('\n') ? text[..^1] : text;
        return string.Join("\n", body.Split('\n').Select(l => l.Length == 0 ? "|" : "| " + l)) + "\n";
    }

    private static string DecodeEntities(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var amp = text.IndexOf('&', i);
            if (amp < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, amp - i);
            if (TryEntity(text, amp, out var replacement, out var length))
            {
                sb.Append(replacement);
                i = amp + length;
            }
            else
            {
                sb.Append('&');
                i = amp + 1;
            }
        }

        return sb.ToString();
    }

    private static bool TryEntity(string text, int at, out string replacement, out int length)
    {
        replacement = string.Empty;
        length = 0;
        var p = at + 1;
        if (p >= text.Length)
        {
            return false;
        }

        if (text[p] == '#')
        {
            var q = p + 1;
            var hex = q < text.Length && text[q] is 'x' or 'X';
            if (hex)
            {
                q++;
            }

            var start = q;
            while (q < text.Length && (hex ? Uri.IsHexDigit(text[q]) && text[q] < 128 : text[q] is >= '0' and <= '9'))
            {
                q++;
            }

            if (q == start || q >= text.Length || text[q] != ';')
            {
                return false;
            }

            var digits = text[start..q];
            var value = BigInteger.Parse(
                hex ? "0" + digits : digits,
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture);
            if (value > 0 && value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF))
            {
                replacement = char.ConvertFromUtf32((int)value);
            }
            else
            {
                replacement = "\uFFFD";
            }

            length = q + 1 - at;
            return true;
        }

        foreach (var (name, value) in new[] { ("amp", "&"), ("lt", "<"), ("gt", ">"), ("quot", "\""), ("apos", "'"), ("nbsp", " ") })
        {
            if (string.CompareOrdinal(text, p, name, 0, name.Length) == 0 && p + name.Length < text.Length && text[p + name.Length] == ';')
            {
                replacement = value;
                length = 1 + name.Length + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>The index of the <c>&gt;</c> ending the tag that opens at <paramref name="from"/>, skipping quoted attribute values; -1 if none.</summary>
    private static int TagEnd(string html, int from)
    {
        char? quote = null;
        for (var i = from + 1; i < html.Length; i++)
        {
            var ch = html[i];
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record Tag(bool End, string Name, Dictionary<string, string> Attrs, bool SelfClosing);

    private static bool IsAttributeNameChar(char c) => !(JsText.IsSpace(c) || c is '"' or '\'' or '=' or '<' or '>' or '/');

    private static Tag? ParseTag(string raw)
    {
        var p = 0;
        var end = false;
        if (p < raw.Length && raw[p] == '/')
        {
            end = true;
            p++;
        }

        while (p < raw.Length && JsText.IsSpace(raw[p]))
        {
            p++;
        }

        if (p >= raw.Length || !(raw[p] is >= 'A' and <= 'Z' || raw[p] is >= 'a' and <= 'z'))
        {
            return null;
        }

        var nameStart = p;
        p++;
        while (p < raw.Length && !(JsText.IsSpace(raw[p]) || raw[p] is '/' or '>'))
        {
            p++;
        }

        var name = JsLower(raw[nameStart..p]);
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = p;
        while (i < raw.Length)
        {
            while (i < raw.Length && !IsAttributeNameChar(raw[i]))
            {
                i++;
            }

            if (i >= raw.Length)
            {
                break;
            }

            var s = i;
            while (i < raw.Length && IsAttributeNameChar(raw[i]))
            {
                i++;
            }

            var attrName = JsLower(raw[s..i]);
            var value = string.Empty;
            var j = i;
            while (j < raw.Length && JsText.IsSpace(raw[j]))
            {
                j++;
            }

            if (j < raw.Length && raw[j] == '=')
            {
                j++;
                while (j < raw.Length && JsText.IsSpace(raw[j]))
                {
                    j++;
                }

                if (j < raw.Length && (raw[j] is '"' or '\''))
                {
                    var close = raw.IndexOf(raw[j], j + 1);
                    if (close >= 0)
                    {
                        value = raw[(j + 1)..close];
                        i = close + 1;
                    }
                }
                else
                {
                    var k = j;
                    while (k < raw.Length && !(JsText.IsSpace(raw[k]) || raw[k] is '"' or '\'' or '=' or '<' or '>' or '`'))
                    {
                        k++;
                    }

                    if (k > j)
                    {
                        value = raw[j..k];
                        i = k;
                    }
                }
            }

            attrs[attrName] = value;
        }

        var trimmedEnd = raw.Length;
        while (trimmedEnd > 0 && JsText.IsSpace(raw[trimmedEnd - 1]))
        {
            trimmedEnd--;
        }

        var selfClosing = trimmedEnd > 0 && raw[trimmedEnd - 1] == '/';
        return new Tag(end, name, attrs, selfClosing);
    }

    private sealed record Visible(string Text, bool Collapsed);

    private static bool IsHeading(string name) => name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6';

    /// <summary>
    /// The text a reader sees in GitHub's rendered HTML, and whether it holds a details/summary start tag. Every <c>&lt;</c> is
    /// markup: comments are dropped, a tag is dropped with its attributes, dropped elements and elements carrying <c>hidden</c>
    /// whole. Anything unterminated drops everything after it.
    /// </summary>
    private static Visible VisibleText(string html)
    {
        var o = new StringBuilder();
        var breaks = 0;
        var prefix = string.Empty;
        var pre = 0;
        (string Name, int Depth)? skip = null;
        var collapsed = false;
        var firstCell = false;
        var lists = new List<(bool Ordered, int N)>();

        void Need(int n) => breaks = Math.Max(breaks, n);

        void Emit(string s, bool verbatim)
        {
            if (!verbatim && (breaks > 0 || o.Length == 0 || o[^1] is ' ' or '\n') && s.StartsWith(' '))
            {
                s = s[1..];
            }

            if (s.Length == 0)
            {
                return;
            }

            if (breaks > 0 && o.Length != 0)
            {
                while (o.Length > 0 && o[^1] == ' ')
                {
                    o.Length--;
                }

                var trailing = 0;
                while (trailing < o.Length && o[o.Length - 1 - trailing] == '\n')
                {
                    trailing++;
                }

                o.Append('\n', Math.Max(0, breaks - trailing));
            }

            breaks = 0;
            o.Append(prefix).Append(s);
            prefix = string.Empty;
        }

        void Text(string raw)
        {
            if (skip is not null || raw.Length == 0)
            {
                return;
            }

            var decoded = DecodeEntities(raw);
            if (pre > 0)
            {
                Emit(decoded, true);
            }
            else
            {
                Emit(CollapseHtmlSpace(decoded), false);
            }
        }

        void Layout(Tag tag)
        {
            if (tag.Name == "pre")
            {
                pre = Math.Max(0, pre + (tag.End ? -1 : 1));
            }

            if (BlockElements.Contains(tag.Name))
            {
                Need(2);
            }
            else if (LineElements.Contains(tag.Name))
            {
                Need(1);
            }

            if (tag.End)
            {
                if (tag.Name is "ul" or "ol" && lists.Count > 0)
                {
                    lists.RemoveAt(lists.Count - 1);
                }

                if (tag.Name == "li" || IsHeading(tag.Name))
                {
                    prefix = string.Empty;
                }

                return;
            }

            if (tag.Name is "ul" or "ol")
            {
                lists.Add((tag.Name == "ol", 0));
            }
            else if (tag.Name == "li")
            {
                if (lists.Count > 0)
                {
                    var last = lists[^1];
                    if (last.Ordered)
                    {
                        last = (true, last.N + 1);
                        lists[^1] = last;
                        prefix = last.N.ToString(CultureInfo.InvariantCulture) + ". ";
                    }
                    else
                    {
                        prefix = "- ";
                    }
                }
            }
            else if (IsHeading(tag.Name))
            {
                prefix = new string('#', tag.Name[1] - '0') + " ";
            }
            else if (tag.Name == "tr")
            {
                firstCell = true;
            }
            else if (tag.Name is "td" or "th")
            {
                if (!firstCell)
                {
                    Emit(" | ", false);
                }

                firstCell = false;
            }
            else if (tag.Name == "input" && tag.Attrs.TryGetValue("type", out var type) && JsLower(type) == "checkbox")
            {
                Emit(tag.Attrs.ContainsKey("checked") ? "[x] " : "[ ] ", false);
            }
            else if (tag.Name == "img")
            {
                Emit("[image]", false);
            }
            else if (tag.Name == "br")
            {
                Emit("\n", true);
            }
        }

        var i = 0;
        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            Text(html[i..(lt < 0 ? html.Length : lt)]);
            if (lt < 0)
            {
                break;
            }

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                var closeComment = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (closeComment < 0)
                {
                    break;
                }

                i = closeComment + 3;
                continue;
            }

            var close = TagEnd(html, lt);
            if (close < 0)
            {
                break;
            }

            i = close + 1;
            var tag = ParseTag(html[(lt + 1)..close]);
            if (tag is null)
            {
                continue;
            }

            if (!tag.End && tag.Name is "details" or "summary")
            {
                collapsed = true;
            }

            var opensSubtree = !tag.End && !tag.SelfClosing && !VoidElements.Contains(tag.Name);
            if (skip is { } active)
            {
                if (tag.Name == active.Name && (tag.End || opensSubtree))
                {
                    active.Depth += tag.End ? -1 : 1;
                    skip = active;
                }

                if (skip is { Depth: 0 })
                {
                    skip = null;
                }

                continue;
            }

            if (!tag.End && (DroppedElements.Contains(tag.Name) || tag.Attrs.ContainsKey("hidden")))
            {
                if (opensSubtree)
                {
                    skip = (tag.Name, 1);
                }

                continue;
            }

            Layout(tag);
        }

        var visible = TrimTrailingSpaces(o.ToString());
        visible = CollapseBlankLines(visible);
        visible = visible.Trim('\n');
        return new Visible(visible, collapsed);
    }

    /// <summary>Each run of space, tab, line feed, carriage return and form feed as one space.</summary>
    private static string CollapseHtmlSpace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (!inRun)
                {
                    sb.Append(' ');
                    inRun = true;
                }
            }
            else
            {
                sb.Append(c);
                inRun = false;
            }
        }

        return sb.ToString();
    }

    /// <summary><c>/ +$/gm</c>: spaces before a line terminator (LF, CR, U+2028, U+2029) or the end are removed.</summary>
    private static string TrimTrailingSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                var j = i;
                while (j < text.Length && text[j] == ' ')
                {
                    j++;
                }

                var atLineEnd = j == text.Length || text[j] is '\n' or '\r' or '\u2028' or '\u2029';
                if (!atLineEnd)
                {
                    sb.Append(text, i, j - i);
                }

                i = j;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary><c>/\n{3,}/g</c> to two line feeds.</summary>
    private static string CollapseBlankLines(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                var j = i;
                while (j < text.Length && text[j] == '\n')
                {
                    j++;
                }

                sb.Append('\n', j - i >= 3 ? 2 : j - i);
                i = j;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }

        return sb.ToString();
    }
}
