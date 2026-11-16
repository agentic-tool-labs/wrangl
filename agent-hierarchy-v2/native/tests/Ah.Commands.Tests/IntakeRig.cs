using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Commands.Tests;

/// <summary>One comment of a fake issue: what the first read lists and what the second read answers.</summary>
internal sealed class IntakeComment(string id, string author, string createdAt, string html)
{
    public string Id { get; } = id;

    public string Author { get; } = author;

    public string CreatedAt { get; } = createdAt;

    public string Html { get; } = html;

    public string? LastEditedAt { get; set; }

    /// <summary>What the second read says the comment was last edited at; the first read's value when null and not <see cref="ChangedOnFetch"/>.</summary>
    public string? FetchLastEditedAt { get; set; }

    public bool ChangedOnFetch { get; set; }

    public bool MissingOnFetch { get; set; }

    /// <summary>The first read lists the comment without any <c>lastEditedAt</c> field at all.</summary>
    public bool OmitLastEditedAt { get; set; }
}

/// <summary>A fake issue as GitHub answers the two reads.</summary>
internal sealed class IntakeIssue
{
    public string State { get; set; } = "OPEN";

    public string? LastEditedAt { get; set; } = "2026-09-19T09:00:00Z";

    public string? FetchLastEditedAt { get; set; }

    public bool ChangedOnFetch { get; set; }

    public List<string> Labels { get; } = ["ready"];

    public List<(string Type, string At, string? Actor, string? Label)> Events { get; } = [("LabeledEvent", "2026-09-20T10:00:00Z", "alice", "ready")];

    public List<(string Type, string At, string? Actor, string? Label)>? FetchEvents { get; set; }

    public List<IntakeComment> Comments { get; } = [];

    public string Title { get; set; } = "T";

    public string BodyHtml { get; set; } = "<p>b</p>";

    public bool GoneOnFetch { get; set; }

    public string? RawFetchOut { get; set; }

    public string? RawFetchErr { get; set; }

    public int? RawFetchStatus { get; set; }

    public static IntakeIssue Eligible(string html = "<p>b</p>", string title = "T") => new() { BodyHtml = html, Title = title };
}

/// <summary>A scripted git and gh around the intake command, with a real directory for the snapshots.</summary>
internal sealed class IntakeRig : IDisposable
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ahprobe.");

    internal string Out => Path.Combine(_root.FullName, "out");

    internal static string Cwd => "/work/proj";

    internal string? Origin { get; set; } = "https://github.com/alice/proj.git";

    internal string? Conventions { get; set; } = "{\"version\":1,\"issues\":{\"trigger_label\":\"ready\"}}";

    internal bool ConventionsAbsent { get; set; }

    internal bool AuthOk { get; set; } = true;

    internal bool GhMissing { get; set; }

    internal string OwnerType { get; set; } = "User";

    internal string OwnerLogin { get; set; } = "alice";

    internal bool OwnerUnreadable { get; set; }

    /// <summary>When set, the metadata read answers no JSON at all and this is what gh printed on standard error.</summary>
    internal string? MetaErrorOnly { get; set; }

    internal List<int[]> LabelledPages { get; } = [];

    internal Dictionary<int, IntakeIssue> Issues { get; } = [];

    internal List<string> GhRequests { get; } = [];

    internal List<string[]> GhArgv { get; } = [];

    internal List<HookErrorRecord> Errors { get; } = [];

    public void Dispose()
    {
        if (_root.FullName.Contains("ahprobe.", StringComparison.Ordinal))
        {
            _root.Delete(recursive: true);
        }
    }

    internal (int Code, string Stdout) Run(params string[] args) => RunWith(null, args);

    internal (int Code, string Stdout) RunWith(string? outDir, params string[] args)
    {
        var all = new List<string>();
        if (args.Length == 0 || args[0] != "--help")
        {
            all.AddRange(["--cwd", Cwd, "--out-dir", outDir ?? Out]);
        }

        all.AddRange(args);
        var runner = new Scripted(Handle);
        var errors = new Collector(Errors);
        var services = new IssueIntakeServices(
            runner,
            PlatformModule.CreateIo(null).Writer,
            new FakeClock(Now),
            ConfigModule.CreateConventions(runner),
            errors);
        var stdout = new StringWriter();
        var code = CommandsModule.RunIssueIntake(services, all, stdout);
        return (code, stdout.ToString());
    }

    /// <summary>The run's standard output with exactly <paramref name="args"/> as its arguments.</summary>
    internal string RunRaw(IReadOnlyList<string> args)
    {
        var runner = new Scripted(Handle);
        var services = new IssueIntakeServices(
            runner,
            PlatformModule.CreateIo(null).Writer,
            new FakeClock(Now),
            ConfigModule.CreateConventions(runner),
            new Collector(Errors));
        var stdout = new StringWriter();
        _ = CommandsModule.RunIssueIntake(services, args, stdout);
        return stdout.ToString();
    }

    internal string SnapshotPath(int number) => Path.Combine(Out, $"issue-{number.ToString(CultureInfo.InvariantCulture)}.md");

    private sealed class Scripted(Func<ProcessRequest, ProcessResult> handler) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request) => handler(request);
    }

    private sealed class Collector(List<HookErrorRecord> rows) : IHookErrorLog
    {
        public void Log(string source, string message) => rows.Add(new HookErrorRecord(source, message));

        public void Log(HookErrorRecord record) => rows.Add(record);
    }

    private static ProcessResult Done(int code, string stdout = "", string stderr = "") =>
        new(code, null, false, false, Encoding.UTF8.GetBytes(stdout), Encoding.UTF8.GetBytes(stderr));

    private ProcessResult Handle(ProcessRequest r)
    {
        if (r.Argv[0] == "git")
        {
            return Git(r.Argv.Skip(1).ToArray());
        }

        if (GhMissing)
        {
            return new ProcessResult(127, null, false, false, default, default);
        }

        GhArgv.Add([.. r.Argv.Skip(1)]);
        var args = r.Argv.Skip(1).ToArray();
        if (args.Length > 0 && args[0] == "auth")
        {
            return AuthOk && args.Contains("github.com") ? Done(0) : Done(1, string.Empty, "not logged in\n");
        }

        var request = Encoding.UTF8.GetString(r.Stdin.Span);
        GhRequests.Add(request);
        var body = JsonText.Parse(request).AsObject();
        var query = body["query"].AsString();
        var v = body["variables"].AsObject();
        var op = query["query ".Length..].Split('(')[0];
        return op switch
        {
            "IntakeOwner" => Owner(v),
            "IntakeLabelled" => Labelled(v),
            "IntakeMeta" => Meta(v),
            "IntakeFetch" => Fetch(v),
            _ => Done(2, string.Empty, "no answer for " + op),
        };
    }

    private ProcessResult Git(string[] args)
    {
        var words = args.Length > 1 && args[0] == "-C" ? args.Skip(2) : args;
        return string.Join(' ', words) switch
        {
            "remote get-url origin" => Origin is null ? Done(1) : Done(0, Origin + "\n"),
            "rev-parse --show-toplevel" => Done(0, Cwd + "\n"),
            "symbolic-ref -q refs/remotes/origin/HEAD" => Done(0, "refs/remotes/origin/main\n"),
            "rev-parse --verify -q refs/remotes/origin/main^{commit}" => Done(0),
            "cat-file -e refs/remotes/origin/main:.claude/ah2/ah-conventions.json" => ConventionsAbsent ? Done(1) : Done(0),
            "cat-file blob refs/remotes/origin/main:.claude/ah2/ah-conventions.json" => Done(0, Conventions ?? string.Empty),
            _ => Done(1),
        };
    }

    private ProcessResult Owner(JsonObject v)
    {
        if (OwnerUnreadable)
        {
            return Done(1, "{\"data\":null}", "owner boom\nsecond line\n");
        }

        return Done(
            0,
            $"{{\"data\":{{\"repository\":{{\"owner\":{{\"__typename\":\"{OwnerType}\",\"login\":\"{OwnerLogin}\"}}}}}}}}");
    }

    private ProcessResult Labelled(JsonObject v)
    {
        var page = v["after"].IsNull ? 0 : int.Parse(v["after"].AsString(), CultureInfo.InvariantCulture);
        var more = page + 1 < LabelledPages.Count;
        var nodes = string.Join(',', LabelledPages[page].Select(n => $"{{\"number\":{n.ToString(CultureInfo.InvariantCulture)}}}"));
        var cursor = more ? $"\"{(page + 1).ToString(CultureInfo.InvariantCulture)}\"" : "null";
        return Done(
            0,
            $"{{\"data\":{{\"repository\":{{\"issues\":{{\"nodes\":[{nodes}],\"pageInfo\":{{\"hasNextPage\":{(more ? "true" : "false")},\"endCursor\":{cursor}}}}}}}}}}}");
    }

    private static string Quote(string? text) => text is null ? "null" : JsonText.Stringify(JsonValue.FromString(text), 0);

    private static string Events(IEnumerable<(string Type, string At, string? Actor, string? Label)> events) =>
        string.Join(
            ',',
            events.Select(e => e.Type == "LabeledEvent"
                ? $"{{\"__typename\":\"LabeledEvent\",\"createdAt\":{Quote(e.At)},\"actor\":{(e.Actor is null ? "null" : "{\"login\":" + Quote(e.Actor) + "}")},\"label\":{{\"name\":{Quote(e.Label)}}}}}"
                : $"{{\"__typename\":\"RenamedTitleEvent\",\"createdAt\":{Quote(e.At)}}}"));

    private static string Common(IntakeIssue i, string? lastEdited, IEnumerable<(string Type, string At, string? Actor, string? Label)> events) =>
        $"\"state\":{Quote(i.State)},\"lastEditedAt\":{Quote(lastEdited)}," +
        $"\"labels\":{{\"nodes\":[{string.Join(',', i.Labels.Select(l => "{\"name\":" + Quote(l) + "}"))}]}}," +
        $"\"timelineItems\":{{\"nodes\":[{Events(events)}]}}";

    private ProcessResult Meta(JsonObject v)
    {
        var number = (int)v["number"].AsNumber();
        if (MetaErrorOnly is not null)
        {
            return Done(1, string.Empty, MetaErrorOnly);
        }

        if (!Issues.TryGetValue(number, out var i))
        {
            return Done(1, "{\"data\":{\"repository\":{\"issue\":null}},\"errors\":[{\"type\":\"NOT_FOUND\"}]}", $"gh: Could not resolve to an Issue with the number of {number.ToString(CultureInfo.InvariantCulture)}.\n");
        }

        var comments = string.Join(
            ',',
            i.Comments.Select(c => $"{{\"id\":{Quote(c.Id)},\"author\":{{\"login\":{Quote(c.Author)}}},\"createdAt\":{Quote(c.CreatedAt)}{(c.OmitLastEditedAt ? string.Empty : ",\"lastEditedAt\":" + Quote(c.LastEditedAt))}}}"));
        return Done(0, $"{{\"data\":{{\"repository\":{{\"issue\":{{{Common(i, i.LastEditedAt, i.Events)},\"comments\":{{\"nodes\":[{comments}]}}}}}}}}}}");
    }

    private ProcessResult Fetch(JsonObject v)
    {
        var number = (int)v["number"].AsNumber();
        var i = Issues[number];
        if (i.RawFetchOut is not null)
        {
            return Done(i.RawFetchStatus ?? 1, i.RawFetchOut, i.RawFetchErr ?? string.Empty);
        }

        if (i.GoneOnFetch)
        {
            return Done(1, "{\"data\":{\"repository\":{\"issue\":null},\"nodes\":[]}}", "gh: Could not resolve to an Issue with the number of 1.\n");
        }

        var edited = i.ChangedOnFetch ? i.FetchLastEditedAt : i.LastEditedAt;
        var ids = v["ids"].AsArray().Select(x => x.AsString()).ToList();
        var nodes = string.Join(
            ',',
            ids.Select(id =>
            {
                var c = i.Comments.FirstOrDefault(x => x.Id == id);
                if (c is null || c.MissingOnFetch)
                {
                    return "null";
                }

                var last = c.ChangedOnFetch ? c.FetchLastEditedAt : c.LastEditedAt;
                return $"{{\"id\":{Quote(c.Id)},\"bodyHTML\":{Quote(c.Html)},\"lastEditedAt\":{Quote(last)}}}";
            }));
        return Done(
            0,
            $"{{\"data\":{{\"repository\":{{\"issue\":{{\"title\":{Quote(i.Title)},\"bodyHTML\":{Quote(i.BodyHtml)},{Common(i, edited, i.FetchEvents ?? i.Events)}}}}},\"nodes\":[{nodes}]}}}}");
    }
}
