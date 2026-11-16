using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Cli.Verbs.Db;

/// <summary>
/// <c>ah db test-seed teams --path &lt;team file&gt; | sessions | mail --dir &lt;box&gt; | acks --dir &lt;box&gt; | peer-rows | gates --dir &lt;dir&gt; | activity --dir &lt;dir&gt; --subject &lt;subject&gt; | peers --dir &lt;dir&gt; | jobs --dir &lt;dir&gt; --team &lt;team folder&gt; --id &lt;id&gt; | home-docs --key &lt;key&gt; | usage-rows</c>: one write through the real op (home-docs and usage-rows: rows stored as given), the JSON on
/// standard input, the way a shell suite puts state down (<c>tests/lib-seed.sh</c>). Test mode only.
/// </summary>
internal static class TestSeedVerb
{
    private const string Usage = "usage: ah db test-seed <teams --path <team file> | sessions | mail --dir <box> | acks --dir <box> | peer-rows | gates --dir <dir> | activity --dir <dir> --subject <subject> | peers --dir <dir> | jobs --dir <dir> --team <team folder> --id <id> | home-docs --key <key> | usage-rows>   (the JSON on standard input; home-docs: the document text, usage-rows: one row per line)";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (!context.Env.TestMode)
        {
            stderr.WriteLine("ah db test-seed: test mode only (AH_TEST=1 and an AH_HOME in an ahprobe sandbox); nothing was written");
            return 2;
        }

        var raw = (context.ReadStdin ?? ReadProcessStdin)();

        // b397 group 2: rows stored verbatim, a planted document or line that does not parse included, as the readers then read them;
        // no service graph is built, so the seed leaves nothing else in the tree
        if (args is ["home-docs", "--key", _] or ["usage-rows"])
        {
            return SeedRows(context.Env.AhHome, args[0] == "usage-rows" ? null : args[2], System.Text.Encoding.UTF8.GetString(raw.Span), stderr);
        }

        if (context.Services?.Value is not { } graph)
        {
            stderr.WriteLine("ah db test-seed: no services here");
            return 1;
        }

        JsonValue input;
        try
        {
            input = JsonText.Parse(raw.Span);
        }
        catch (JsonParseException e)
        {
            stderr.WriteLine($"ah db test-seed: standard input is not JSON: {e.Message}");
            return 2;
        }

        var cwd = Directory.GetCurrentDirectory();
        (string Domain, string Op, JsonValue Args, string Dir)? call = args switch
        {
            ["teams", "--path", var path] => ("team", "write", new JsonObject { ["path"] = path, ["dir"] = PoolOf(path), ["expect"] = graph.Roster.ReadTeamWithDigest(path)["digest"], ["team"] = input }, PoolOf(path)),
            ["sessions"] => ("mailbox", "register", input, cwd),
            ["mail", "--dir", var box] => ("mailbox", "append", new JsonObject { ["dir"] = box, ["entry"] = input }, cwd),
            ["acks", "--dir", var box] => ("mailbox", "ack", new JsonObject { ["dir"] = box, ["id"] = input.Member("id") ?? JsonValue.Null, ["by"] = input.Member("by") ?? JsonValue.Null, ["session_id"] = input.Member("session_id") ?? JsonValue.Null }, cwd),
            ["peer-rows"] => ("peer-records", "append", new JsonObject { ["rec"] = input }, cwd),
            ["gates", "--dir", var gatesDir] => ("gates", "put", new JsonObject { ["dir"] = gatesDir, ["body"] = JsonText.Stringify(input, 0) }, gatesDir),
            ["activity", "--dir", var activityDir, "--subject", var subject] => ("activity", "put", new JsonObject { ["dir"] = activityDir, ["subject"] = subject, ["body"] = JsonText.Stringify(input, 0) }, activityDir),
            ["peers", "--dir", var peersDir] => ("peers", "put", new JsonObject { ["dir"] = peersDir, ["body"] = JsonText.Stringify(input, 0) }, peersDir),
            ["jobs", "--dir", var jobsDir, "--team", var jobTeam, "--id", var jobId] => ("jobs", "put", new JsonObject { ["dir"] = jobsDir, ["team"] = jobTeam, ["id"] = jobId, ["record"] = input }, jobsDir),
            _ => null,
        };
        if (call is not { } c)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        _ = Directory.CreateDirectory(args[0] == "teams" ? Path.GetDirectoryName(args[2])! : c.Dir);
        var result = graph.Writer.Invoke(c.Domain, c.Op, JsonText.ToUtf8(c.Args, 0), new RequestContext(c.Dir, c.Dir, string.Empty, string.Empty));
        var answer = result.Ok ? JsonText.Parse(result.ResultJson.Span) : JsonValue.Null;
        if (!result.Ok || answer.Member("conflict") is { IsTruthy: true } || (args[0] == "sessions" && answer.Member("registered") is not { IsTruthy: true }))
        {
            stderr.WriteLine($"ah db test-seed: {c.Domain} {c.Op} refused: {result.Error ?? result.ErrorCode ?? (answer.Member("conflict") is { IsTruthy: true } ? "conflict" : "not registered")}");
            return 1;
        }

        // a planted `at` is kept: the register op stamps now, and a case staging an old registration needs its age
        if (args[0] == "sessions" && input.Member("at") is { IsText: true } at && input.Member("session_id") is { IsText: true } sid)
        {
            var sessions = Ah.StateDb.StateDbModule.OpenSessionAuthority(context.Env.AhHome);
            if (sessions.ReadStored(sid.AsString()) is not { } held || JsonText.Parse(held.Span) is not { IsMap: true } doc)
            {
                stderr.WriteLine("ah db test-seed: the registration is not in the store, so its at was not kept");
                return 1;
            }

            var record = doc.AsObject();
            record["at"] = at;
            var kept = sessions.Register(graph.Mailbox.RegistrationFile(sid.AsString()), sid.AsString(), _ => new SessionDecision(SessionVerdict.Write, System.Text.Encoding.UTF8.GetBytes(JsonText.Stringify(record, 0) + "\n")));
            if (!kept.Ok)
            {
                stderr.WriteLine($"ah db test-seed: the registration's at was not kept: {kept.Message}");
                return 1;
            }
        }

        stdout.WriteLine(System.Text.Encoding.UTF8.GetString(result.ResultJson.Span));
        return 0;
    }

    private static readonly string[] HomeDocKeys = ["home.ultra-gate", "home.session-roles", "home.permissions", Ah.Watch.WatchModule.RecoverySwitchKey, Ah.Roster.RosterModule.CodexTrustPreapprovalKey];

    /// <summary>
    /// <c>home-docs --key &lt;key&gt;</c> stores <paramref name="text"/> as the key's <c>meta</c> row; <c>usage-rows</c> (null <paramref name="key"/>)
    /// stores each non-empty line as a <c>usage_rows</c> row, in order.
    /// </summary>
    private static int SeedRows(string ahHome, string? key, string text, TextWriter stderr)
    {
        if (key is not null && !HomeDocKeys.Contains(key))
        {
            stderr.WriteLine($"ah db test-seed: home-docs takes one of {string.Join(", ", HomeDocKeys)}");
            return 2;
        }

        var outcome = Ah.StateDb.StateDbModule.CreateOpRunner(ahHome).Run(new DbOpRequest(key is null ? "usage" : "home-docs", null, ahHome, tx =>
        {
            if (key is not null)
            {
                tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", key, text);
            }
            else
            {
                foreach (var line in text.Split('\n').Where(l => l.Length > 0))
                {
                    tx.Execute("INSERT INTO usage_rows(body) VALUES (?1)", line);
                }
            }

            return new OpResult(true, "{}"u8.ToArray());
        }, LeavesSeq: true, DbOnly: true));
        if (outcome.Status != DbOpStatus.Done)
        {
            stderr.WriteLine($"ah db test-seed: {(key ?? "usage-rows")} refused: {outcome.Error ?? outcome.Status.ToString()}");
            return 1;
        }

        return 0;
    }

    /// <summary>The hierarchy directory a team file belongs to: <c>&lt;dir&gt;/teams/x.json</c> or the legacy <c>&lt;dir&gt;/team.json</c>.</summary>
    private static string PoolOf(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        return Path.GetFileName(parent) == "teams" ? Path.GetDirectoryName(parent)! : parent;
    }

    private static ReadOnlyMemory<byte> ReadProcessStdin()
    {
        using var input = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }
}
