using System.Reflection;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// Runs the <c>lib-mailbox</c> routing tables (address resolution, send, inbox, append) against the native mailbox. A box directory
/// carries a hash of its team file's real path, which differs in every sandbox, so a scenario names boxes by team and member and
/// every hash in a result is written <c>~HASH</c>; the Node shim does the same.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class MailboxRoutingUnitTableTests
{
    private static readonly string NativeRoot = typeof(MailboxRoutingUnitTableTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!;

    private static readonly string UnitsRoot = Path.Combine(NativeRoot, "..", "tests", "contracts", "units");

    internal static readonly string[] Functions =
    [
        "scenario", "visibleTeams", "membersNamed", "chooseFromJson", "relayLine", "renderSend", "renderInbox",
    ];

    [GeneratedRegex("~[0-9a-f]{10}(?=[/\"\\\\]|$)")]
    private static partial Regex HashRe();

    [GeneratedRegex("20260101-120000-[a-z0-9]{4}")]
    private static partial Regex MintedRe();

    [GeneratedRegex(@"(its record could not be written) \(.*\)$", RegexOptions.Singleline)]
    private static partial Regex CauseRe();

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in TableFiles())
        {
            foreach (var c in UnitTable.Load(file).Cases)
            {
                data.Add(Path.GetRelativePath(UnitsRoot, file), c.Id);
            }
        }

        return data;
    }

    private static IEnumerable<string> TableFiles() =>
        UnitTable.TableFiles(NativeRoot).Where(f => f.Contains("/lib-mailbox/", StringComparison.Ordinal) && Functions.Contains(UnitTable.Load(f).Fn));

    [Fact]
    public void TablesCoverEveryDispatchedFunction()
    {
        var present = TableFiles().Select(f => UnitTable.Load(f).Fn).ToHashSet();

        Assert.All(Functions, fn => Assert.Contains(fn, present));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CaseMatchesNode(string tableFile, string caseId)
    {
        var table = UnitTable.Load(Path.Combine(UnitsRoot, tableFile));
        var c = table.Cases.Single(x => x.Id == caseId);
        var sandbox = UnitSandbox.Create();
        sandbox.WriteFiles(c);
        JsonValue? result = null;
        var threw = false;
        try
        {
            result = Dispatch(table.Fn, c, sandbox);
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.True(sandbox.Verify(c, result, threw) is null, $"{table.Fn} / {c.Id}: {sandbox.Verify(c, result, threw)}");
    }

    private static JsonValue? Dispatch(string fn, UnitCase c, UnitSandbox sandbox)
    {
        var fx = MeshFixture.Create(
            sandbox.Environment(c),
            c.Now is { } now ? DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow,
            sandbox.Root);
        var mailbox = fx.Mailbox;

        JsonValue? Arg(int i) => i < c.Args.Count ? sandbox.Decode(c.Args[i]) : null;

        switch (fn)
        {
            case "scenario":
            {
                var o = Arg(0)!.Value.AsObject();
                var ctx = o.Get("ctx")!.Value.AsObject();
                var results = new JsonArray();
                foreach (var step in o.Get("steps")!.Value.AsArray())
                {
                    results.Add(Normalise(Step(fx, ctx, step.AsObject())));
                }

                return results;
            }

            case "visibleTeams":
            {
                var list = new JsonArray();
                foreach (var t in mailbox.VisibleTeams(ContextOf(Arg(0)!.Value.AsObject())))
                {
                    list.Add(new JsonObject { ["team"] = t.Team, ["path"] = t.Path, ["data"] = t.Data, ["live"] = t.Live });
                }

                return list;
            }

            case "membersNamed":
            {
                var list = new JsonArray();
                foreach (var m in mailbox.MembersNamed(ContextOf(Arg(0)!.Value.AsObject()), Arg(1)!.Value.AsString()))
                {
                    list.Add(new JsonObject
                    {
                        ["kind"] = "member",
                        ["name"] = m.Name,
                        ["team"] = m.Team,
                        ["role"] = m.Role is null ? JsonValue.Null : m.Role,
                        ["address"] = m.Address,
                    });
                }

                return list;
            }

            case "chooseFromJson":
            {
                var o = Arg(0)!.Value.AsObject();
                var callers = o.Get("callerAddresses")!.Value.AsArray().Select(a => a.AsString()).ToList();
                var explicitFrom = o.Get("explicit") is { IsText: true } e ? e.AsString() : null;
                var team = o.Get("toTeam") is { IsText: true } t ? t.AsString() : null;
                return Wrapped(mailbox.ChooseFrom(callers, team, explicitFrom), v => v!);
            }

            case "relayLine":
                return SenderHeaderRe().Replace(mailbox.RelayLine(Arg(0)!.Value, Arg(1)!.Value.AsString()), string.Empty);
            case "renderSend":
                return mailbox.RenderSend(Arg(0)!.Value.AsObject());
            case "renderInbox":
                return mailbox.RenderInbox(Arg(0)!.Value.AsObject().Get("entries")!.Value.AsArray().ToList());
            default:
                throw new NotSupportedException("no native dispatcher for " + fn);
        }
    }

    private static RequestContext ContextOf(JsonObject ctx) => new(
        ctx.Get("cwd") is { IsText: true } c ? c.AsString() : string.Empty,
        ctx.Get("hierarchy_dir") is { IsText: true } h ? h.AsString() : string.Empty,
        ctx.Get("main_hierarchy_dir") is { IsText: true } m ? m.AsString() : string.Empty,
        ctx.Get("messaging_socket_dir") is { IsText: true } s ? s.AsString() : string.Empty);

    private static JsonValue Wrapped<T>(MailboxOutcome<T> outcome, Func<T?, JsonValue> toJson)
    {
        if (outcome.Failure is { } f)
        {
            var error = new JsonObject { ["code"] = f.Code, ["message"] = f.Message };
            if (f.Candidates is { } candidates)
            {
                var list = new JsonArray();
                foreach (var cand in candidates)
                {
                    list.Add(cand);
                }

                error["candidates"] = list;
            }

            return new JsonObject { ["error"] = error };
        }

        return new JsonObject { ["ok"] = toJson(outcome.Value) };
    }

    /// <summary>A process's start time as the system <c>ps</c> prints it; null for a process that is not there.</summary>
    private static string? StartOf(long pid)
    {
        var info = new System.Diagnostics.ProcessStartInfo("/bin/ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add("lstart=");
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.Environment["LC_ALL"] = "C";
        using var process = System.Diagnostics.Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && text.Length > 0 ? text : null;
    }

    private static CallerRequest CallerOf(JsonObject ctx, JsonObject s) => new(
        s.Get("cwd") is { IsText: true } cwd ? cwd.AsString() : ctx.Get("cwd")!.Value.AsString(),
        s.Get("session_id") is { IsText: true } sid ? sid.AsString() : null,
        s.Get("pid") is { IsNumber: true } pid ? (int)pid.AsNumber() : null,
        s.Get("pane_id") is { IsText: true } pane ? pane.AsString() : null,
        s.Get("team_arg") is { IsText: true } team ? team.AsString() : null,
        s.Get("team_default_explicit") is { IsTruthy: true });

    private static JsonArray CallerBoxesJson(IReadOnlyList<CallerBox> boxes)
    {
        var list = new JsonArray();
        foreach (var b in boxes)
        {
            var o = new JsonObject { ["address"] = b.Address };
            if (b.Team is not null)
            {
                o["team"] = b.Team;
                o["name"] = b.Name ?? string.Empty;
                o["teamFile"] = b.TeamFile ?? string.Empty;
            }

            o["dir"] = b.Dir;
            list.Add(o);
        }

        return list;
    }

    private static string TeamFile(JsonObject ctx, string team) =>
        Ah.Core.Text.NodePath.Join(ctx.Get("hierarchy_dir")!.Value.AsString(), "teams", team + ".json");

    private static string BoxOf(IMailbox mailbox, JsonObject ctx, JsonObject step) =>
        mailbox.BoxDirFor(TeamFile(ctx, step.Get("team")!.Value.AsString()), step.Get("name")!.Value.AsString());

    private static JsonObject RequestOf(JsonObject ctx, JsonObject s)
    {
        var req = new JsonObject();
        foreach (var key in ctx.Keys)
        {
            req[key] = ctx[key];
        }

        var over = s.Get("req")!.Value.AsObject();
        foreach (var key in over.Keys)
        {
            req[key] = over[key];
        }

        return req;
    }

    // a table names message files; a message written is a row, so it is written out for the comparison
    private static void MirrorMessages(MeshFixture fx, string path)
    {
        if (path.Contains("/msgs", StringComparison.Ordinal))
        {
            TestMsgStore.MirrorOut(fx.Writer.AhHome);
        }
    }

    private static JsonValue Step(MeshFixture fx, JsonObject ctx, JsonObject s)
    {
        var mailbox = fx.Mailbox;
        var requestContext = ContextOf(ctx);
        var op = s.Get("op")!.Value.AsString();
        switch (op)
        {
            case "resolve":
            {
                var to = s.Get("to") is { IsTruthy: true } t ? JsConvert.ToJsString(t) : null;
                return Wrapped(mailbox.ResolveAddress(to, requestContext), r => new JsonObject
                {
                    ["name"] = r!.Name,
                    ["team"] = r.Team,
                    ["address"] = r.Address,
                    ["member"] = r.Member ?? JsonValue.Null,
                    ["orchestrator"] = r.Orchestrator ?? JsonValue.Null,
                    ["teamPath"] = r.TeamPath,
                    ["home"] = r.Home is null ? JsonValue.Null : r.Home,
                    ["dir"] = r.Dir,
                });
            }

            case "send":
                return Wrapped(mailbox.Send(RequestOf(ctx, s)), v => v!);
            case "meshSend":
                return Wrapped(fx.Sender.Send(RequestOf(ctx, s)), v => v!);
            case "gate":
            {
                var request = new MeshGateRequest(
                    requestContext,
                    s.Get("session_id") is { IsText: true } gateSession ? gateSession.AsString() : null,
                    s.Get("to") is { IsText: true } to ? to.AsString() : null,
                    s.Get("text") is { IsText: true } text ? text.AsString() : string.Empty,
                    s.Get("msgKind") is { IsText: true } kind ? kind.AsString() : "sendmessage",
                    s.Get("now") is { IsNumber: true } nowMs ? nowMs.AsNumber() : null);
                var outcome = fx.Gate.Check(request);
                if (outcome.Failure is not null)
                {
                    return Wrapped(outcome, _ => JsonValue.Null);
                }

                if (outcome.Value is not { } gated)
                {
                    return new JsonObject { ["ok"] = JsonValue.Null };
                }

                var body = MeshJson.Verdict(gated.Verdict);
                body["hasAfter"] = gated.After is not null;
                var result = new JsonObject { ["ok"] = body };
                if (s.Get("runAfter") is { IsTruthy: true } && gated.After is { } after)
                {
                    try
                    {
                        after();
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException)
                    {
                        result["afterError"] = e.Message;
                    }
                }

                return result;
            }

            case "linkEnvelope":
            {
                var line = fx.Gate.LinkEnvelopeFor(
                    requestContext,
                    s.Get("session_id") is { IsText: true } envelopeSession ? envelopeSession.AsString() : null,
                    s.Get("from") is { IsText: true } from ? from.AsString() : null,
                    s.Get("now") is { IsNumber: true } nowMs ? nowMs.AsNumber() : null);
                return new JsonObject { ["ok"] = line is null ? JsonValue.Null : line };
            }

            case "revoke":
            {
                var names = s.Get("names") is { IsArray: true } list ? list.AsArray().Select(n => n.AsString()).ToList() : null;
                fx.Gate.RevokeLinksFor(TeamFile(ctx, s.Get("team")!.Value.AsString()), names);
                return new JsonObject();
            }

            case "hfile":
            {
                var hier = ctx.Get("hierarchy_dir")!.Value.AsString();
                var rel = s.Get("rel")!.Value.AsString();

                // a team's links record is a row of the links table (per-dir-files-to-db S11b), shown as the file it was
                if (rel.StartsWith("links/", StringComparison.Ordinal) && rel.EndsWith(".json", StringComparison.Ordinal))
                {
                    return new JsonObject { ["ok"] = TestLinks.Text(fx.Environment.AhHome, hier, rel["links/".Length..^".json".Length]) is { } row ? row : JsonValue.Null };
                }

                var path = Ah.Core.Text.NodePath.Join(hier, rel);
                MirrorMessages(fx, path);
                return new JsonObject { ["ok"] = File.Exists(path) ? Ah.Core.Text.Utf8Text.Decode(File.ReadAllBytes(path)) : JsonValue.Null };
            }

            case "ackFromPrompt":
            {
                var r = fx.Hooks.AckFromPrompt(
                    s.Get("session_id") is { IsText: true } promptSession ? promptSession.AsString() : null,
                    PromptOf(fx, ctx, s.Get("prompt")));
                return new JsonObject
                {
                    ["ok"] = new JsonObject { ["acked"] = Strings(r.Acked), ["fresh"] = Strings(r.Fresh), ["block"] = r.Duplicate },
                };
            }

            case "ackFromSendMessage":
            {
                var acked = fx.Hooks.AckFromSendMessage(
                    s.Get("session_id") is { IsText: true } messageSession ? messageSession.AsString() : null,
                    s.Get("to") is { IsText: true } toText ? toText.AsString() : null,
                    s.Get("message") is { IsText: true } messageText ? messageText.AsString() : null,
                    requestContext);
                return new JsonObject { ["ok"] = Strings(acked) };
            }

            case "openBriefs":
            {
                var list = new JsonArray();
                foreach (var b in fx.Hooks.OpenMailBriefs(s.Get("session_id") is { IsText: true } briefSession ? briefSession.AsString() : null))
                {
                    var row = new JsonObject { ["id"] = b.Id, ["ref"] = b.Ref };
                    if (b.From is { } from)
                    {
                        row["from"] = from;
                    }

                    list.Add(row);
                }

                return new JsonObject { ["ok"] = list };
            }

            case "summary":
            {
                var summary = fx.Hooks.Summary();
                var rows = new JsonArray();
                foreach (var r in summary.Registrations)
                {
                    rows.Add(new JsonObject { ["address"] = r.Address, ["harness"] = r.Harness is null ? JsonValue.Null : r.Harness, ["adapter"] = r.Adapter });
                }

                return new JsonObject { ["ok"] = new JsonObject { ["registrations"] = rows, ["overdue"] = summary.Overdue } };
            }

            case "inspectBoxes":
                return new JsonObject { ["ok"] = fx.Inspect.Boxes(s.Get("address") is { IsText: true } boxAddress ? boxAddress.AsString() : null) };
            case "inspectEntries":
            {
                var n = s.Get("n") is { IsNumber: true } count ? (int)count.AsNumber() : IMailInspect.MaxEntries;
                var filter = s.Get("filter") is { IsText: true } f ? f.AsString() : "pending";
                var name = s.Get("name")!.Value.AsString();
                var team = s.Get("team")!.Value.AsString();
                return new JsonObject { ["ok"] = fx.Inspect.Entries(BoxOf(mailbox, ctx, s), $"{name}@{team}", filter, n) };
            }

            case "projectEntries":
            {
                var listed = EntriesJson(mailbox.ReadEntries(BoxOf(mailbox, ctx, s), 0));
                var projected = new JsonArray();
                if (listed.Get("entries") is { IsArray: true } listedEntries)
                {
                    foreach (var entry in listedEntries.AsArray())
                    {
                        var row = new JsonObject();
                        foreach (var key in new[] { "id", "kind", "from", "to", "ref", "link" })
                        {
                            row[key] = entry.Member(key) ?? JsonValue.Null;
                        }

                        projected.Add(row);
                    }
                }

                return new JsonObject { ["ok"] = projected };
            }

            case "inspectRegistrations":
                return new JsonObject { ["ok"] = fx.Inspect.Registrations() };
            case "inspectEvents":
                return new JsonObject { ["ok"] = fx.Inspect.Events(s.Get("address") is { IsText: true } eventAddress ? eventAddress.AsString() : null) };
            case "relay":
            {
                var dirs = s.Get("boxes")!.Value.AsArray().Select(b => BoxOf(mailbox, ctx, b.AsObject())).ToList();
                var ids = s.Get("ids")!.Value.AsArray().Select(i => i.AsString()).ToList();
                var count = fx.Relay.ObligationsFor(
                    s.Get("session_id") is { IsText: true } relaySession ? relaySession.AsString() : null,
                    dirs,
                    ids,
                    s.Get("by")!.Value.AsString());
                return new JsonObject { ["ok"] = count };
            }

            case "peerRecords":
            {
                var rows = new JsonArray();
                foreach (var row in fx.Hierarchy.Core.ReadPeerRecords())
                {
                    var copy = new JsonObject();
                    foreach (var key in row.AsObject().Keys)
                    {
                        if (key == "op_id")
                        {
                            continue;
                        }

                        copy[key] = key == "ts" && row.AsObject()[key].IsText ? "<ts>" : row.AsObject()[key];
                    }

                    rows.Add(copy);
                }

                return new JsonObject { ["ok"] = rows };
            }

            case "linkStates":
            {
                var states = new JsonArray();
                if (TestLinks.Text(fx.Environment.AhHome, ctx.Get("hierarchy_dir")!.Value.AsString(), s.Get("team")!.Value.AsString()) is { } body
                    && JsonText.Parse(body).AsObject().Get("links") is { IsArray: true } links)
                {
                    foreach (var l in links.AsArray())
                    {
                        var o = l.AsObject();
                        var row = new JsonArray();
                        row.Add(o.Get("id") ?? JsonValue.Null);
                        row.Add(o.Get("state") ?? JsonValue.Null);
                        row.Add(o.Get("end_reason") ?? JsonValue.Null);
                        row.Add(o.Get("used") ?? JsonValue.Null);
                        states.Add(row);
                    }
                }

                return new JsonObject { ["ok"] = states };
            }

            case "hlist":
            {
                var path = Ah.Core.Text.NodePath.Join(ctx.Get("hierarchy_dir")!.Value.AsString(), s.Get("rel")!.Value.AsString());
                MirrorMessages(fx, path);
                var names = new JsonArray();
                if (Directory.Exists(path))
                {
                    foreach (var name in Directory.GetFileSystemEntries(path).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                    {
                        names.Add(name!);
                    }
                }

                return new JsonObject { ["ok"] = names };
            }

            case "heartbeat":
                mailbox.WriteHeartbeat(
                    BoxOf(mailbox, ctx, s),
                    s.Get("adapter")!.Value.AsString(),
                    s.Get("session_id") is { IsText: true } sid ? sid.AsString() : null,
                    s.Get("poll_ms")!.Value.AsNumber());

                // the library call returns nothing, which a result records as an object with no `ok`
                return new JsonObject();
            case "register":
            {
                var boxes = s.Get("boxes")!.Value.AsArray()
                    .Select(b => new BoxRef($"{b.AsObject().Get("name")!.Value.AsString()}@{b.AsObject().Get("team")!.Value.AsString()}", BoxOf(mailbox, ctx, b.AsObject())))
                    .ToList();
                var write = new RegistrationWrite(
                    s.Get("session_id")!.Value.AsString(),
                    boxes,
                    TeamFile: s.Get("team_file") is { IsText: true } tf ? tf.AsString() : null,
                    Harness: s.Get("harness") is { } h ? (h.IsText ? h.AsString() : null) : "claude",
                    Pid: s.Get("pid") is { IsNumber: true } pid ? (long)pid.AsNumber() : null,
                    PaneId: s.Get("pane_id") is { IsText: true } pane ? pane.AsString() : null,
                    Cwd: s.Get("cwd") is { IsText: true } cwd ? cwd.AsString() : null,
                    PidStart: s.Get("pid") is { IsNumber: true } started ? StartOf((long)started.AsNumber()) : null);
                return new JsonObject { ["ok"] = mailbox.WriteRegistration(write) };
            }

            case "ack":
            {
                var ids = s.Get("ids")!.Value.AsArray().Select(i => i.AsString()).ToList();
                var r = mailbox.AckIds(BoxOf(mailbox, ctx, s), ids, s.Get("by")!.Value.AsString(), s.Get("session_id") is { IsText: true } a ? a.AsString() : null);
                return new JsonObject
                {
                    ["ok"] = new JsonObject { ["acked"] = Strings(r.Acked), ["unknown"] = Strings(r.Unknown), ["fresh"] = Strings(r.Fresh) },
                };
            }

            case "inbox":
            {
                var opts = s.Get("opts") is { IsMap: true } m ? m.AsObject() : new JsonObject();
                var result = mailbox.Inbox(
                    BoxOf(mailbox, ctx, s),
                    new InboxOptions(
                        All: opts.Get("all") is { IsTruthy: true },
                        Overdue: opts.Get("overdue") is { IsTruthy: true },
                        Since: opts.Get("since") is { IsNumber: true } since ? (long)since.AsNumber() : 0));
                return new JsonObject { ["ok"] = EntriesJson(result) };
            }

            case "entries":
            {
                var from = s.Get("from") is { IsNumber: true } f ? (long)f.AsNumber() : 0;
                return new JsonObject { ["ok"] = EntriesJson(mailbox.ReadEntries(BoxOf(mailbox, ctx, s), from)) };
            }

            case "append":
            {
                var attempt = s.Get("attempt") is { IsMap: true } at ? at.AsObject() : null;
                return Wrapped(mailbox.AppendEntry(BoxOf(mailbox, ctx, s), s.Get("entry")!.Value.AsObject(), attempt), v => v!);
            }

            case "attempts":
            {
                var list = new JsonArray();
                foreach (var row in mailbox.ReadAttempts(BoxOf(mailbox, ctx, s)))
                {
                    list.Add(row);
                }

                return new JsonObject { ["ok"] = list };
            }

            case "write":
            {
                var path = Ah.Core.Text.NodePath.Join(BoxOf(mailbox, ctx, s), s.Get("rel")!.Value.AsString());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Ah.Core.Text.Utf8Text.Encode(s.Get("text")!.Value.AsString()));
                return new JsonObject { ["ok"] = true };
            }

            case "file":
            {
                var path = Ah.Core.Text.NodePath.Join(BoxOf(mailbox, ctx, s), s.Get("rel")!.Value.AsString());
                return new JsonObject { ["ok"] = File.Exists(path) ? Ah.Core.Text.Utf8Text.Decode(File.ReadAllBytes(path)) : JsonValue.Null };
            }

            case "writeTeam":
            {
                var path = TeamFile(ctx, s.Get("team")!.Value.AsString());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Ah.Core.Text.Utf8Text.Encode(JsonText.Stringify(s.Get("doc")!.Value, 0) + "\n"));
                return new JsonObject { ["ok"] = true };
            }

            case "requestContext":
            {
                var r = fx.Callers.RequestContext(s.Get("cwd") is { IsText: true } cwd ? cwd.AsString() : ctx.Get("cwd")!.Value.AsString());
                return new JsonObject
                {
                    ["ok"] = new JsonObject
                    {
                        ["cwd"] = r.Cwd,
                        ["hierarchy_dir"] = r.HierarchyDirectory,
                        ["main_hierarchy_dir"] = r.MainHierarchyDirectory.Length > 0 ? r.MainHierarchyDirectory : JsonValue.Null,
                        ["messaging_socket_dir"] = r.SocketDirectory.Length > 0 ? r.SocketDirectory : JsonValue.Null,
                    },
                };
            }

            case "resolveCaller":
                return new JsonObject { ["ok"] = CallerBoxesJson(fx.Callers.ResolveCaller(CallerOf(ctx, s))) };
            case "registerCaller":
                return new JsonObject { ["ok"] = CallerBoxesJson(fx.Callers.RegisterCaller(CallerOf(ctx, s))) };
            case "ensureRegistered":
                return new JsonObject { ["ok"] = CallerBoxesJson(fx.Callers.EnsureRegistered(CallerOf(ctx, s))) };
            case "callerBoxes":
                return new JsonObject { ["ok"] = CallerBoxesJson(fx.Callers.CallerBoxes(CallerOf(ctx, s))) };
            case "joinDeciders":
            {
                var list = new JsonArray();
                foreach (var d in fx.Callers.JoinDeciderBoxes(ctx.Get("cwd")!.Value.AsString(), s.Get("session_id") is { IsText: true } decider ? decider.AsString() : null))
                {
                    list.Add(new JsonObject { ["address"] = d.Address, ["team"] = d.Team, ["session"] = d.Session is null ? JsonValue.Null : d.Session });
                }

                return new JsonObject { ["ok"] = list };
            }

            case "ackRouted":
            {
                var ids = s.Get("ids")!.Value.AsArray().Select(i => i.AsString()).ToList();
                var r = fx.Callers.AckRouted(BoxOf(mailbox, ctx, s), ids, s.Get("by")!.Value.AsString(), s.Get("session_id") is { IsText: true } a ? a.AsString() : null);
                return new JsonObject
                {
                    ["ok"] = new JsonObject { ["acked"] = Strings(r.Acked), ["unknown"] = Strings(r.Unknown), ["fresh"] = Strings(r.Fresh) },
                };
            }

            case "registration":
            {
                var reg = mailbox.ReadRegistration(s.Get("session_id")!.Value.AsString());
                if (reg is not null)
                {
                    // the native registration also records the process start time and whether it is bound; the Node one has neither
                    reg = reg.Clone();
                    reg.Remove("pid_start");
                    reg.Remove("bound");
                    reg.Remove("teams_stamp"); // b356: team-file mtimes, volatile per run
                }

                if (reg is not null && reg.Get("pid") is { IsNumber: true } own && own.AsNumber() == System.Environment.ProcessId)
                {
                    reg = reg.Clone();
                    reg["pid"] = "<selfpid>";
                }

                return new JsonObject { ["ok"] = reg is null ? JsonValue.Null : reg };
            }

            case "box":
                return new JsonObject { ["ok"] = BoxOf(mailbox, ctx, s) };
            default:
                throw new NotSupportedException("no native step " + op);
        }
    }

    /// <summary>A prompt spec of a scenario: a literal, the adapter's prompt for entries of a box, or their relay lines in a cross-session wrapper.</summary>
    private static string? PromptOf(MeshFixture fx, JsonObject ctx, JsonValue? spec)
    {
        if (spec is { IsText: true } literal)
        {
            return literal.AsString();
        }

        if (spec is not { IsMap: true } m)
        {
            return null;
        }

        JsonObject Source(JsonObject o) => o;
        List<JsonValue> Entries(JsonObject o) => fx.Mailbox.ReadEntries(BoxOf(fx.Mailbox, ctx, o)).Entries.ToList();

        if (m.AsObject().Get("build") is { IsMap: true } build)
        {
            var b = Source(build.AsObject());
            var all = Entries(b);
            var chosen = b.Get("ids")!.Value.AsArray().Select(id => all.FirstOrDefault(e => e.AsObject().Get("id")!.Value.AsString() == id.AsString())).Where(e => e.IsMap).ToList();
            return fx.Mailbox.BuildPrompt($"{b.Get("name")!.Value.AsString()}@{b.Get("team")!.Value.AsString()}", chosen);
        }

        if (m.AsObject().Get("wrap") is { IsMap: true } wrap)
        {
            var w = Source(wrap.AsObject());
            var all = Entries(w);
            var address = w.Get("address") is { IsText: true } a ? a.AsString() : $"{w.Get("name")!.Value.AsString()}@{w.Get("team")!.Value.AsString()}";
            var suffix = w.Get("suffix") is { IsText: true } sfx ? sfx.AsString() : string.Empty;
            var lines = new List<string>();
            foreach (var id in w.Get("ids")!.Value.AsArray())
            {
                JsonValue entry = w.Get("id") is { IsText: true } fake
                    ? new JsonObject { ["id"] = fake.AsString(), ["text"] = "t" }
                    : all.First(e => e.AsObject().Get("id")!.Value.AsString() == id.AsString());
                lines.Add(fx.Mailbox.RelayLine(entry, address) + suffix);
            }

            if (w.Get("prefix") is { IsText: true } prefix)
            {
                lines[0] = prefix.AsString() + lines[0];
            }

            return $"<cross-session-message from=\"uds:/tmp/cc-socks/1.sock\">\n{string.Join("\n", lines)}\n</cross-session-message>";
        }

        return null;
    }

    private static JsonObject EntriesJson(EntriesResult r)
    {
        var list = new JsonArray();
        foreach (var e in r.Entries)
        {
            list.Add(e);
        }

        return new JsonObject { ["entries"] = list, ["next"] = r.Next };
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        var list = new JsonArray();
        foreach (var v in values)
        {
            list.Add(v);
        }

        return list;
    }

    /// <summary>Writes every box hash as <c>~HASH</c>, in strings and in object keys.</summary>
    /// <summary>The sender, recipient and kind this build puts in front of a relay line; the recorded Node text has none.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\Aah mail from \S+ to \S+( \([A-Za-z-]+\))?: ", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SenderHeaderRe();

    private static JsonValue Normalise(JsonValue v)
    {
        if (v.IsText)
        {
            var text = MintedRe().Replace(HashRe().Replace(SenderHeaderRe().Replace(v.AsString(), string.Empty), "~HASH"), "<new>");
            return CauseRe().Replace(text.Replace(MeshFixture.PluginRoot, "$AHROOT", StringComparison.Ordinal), "$1 (<cause>)");
        }

        if (v.IsArray)
        {
            var list = new JsonArray();
            foreach (var item in v.AsArray())
            {
                list.Add(Normalise(item));
            }

            return list;
        }

        if (v.IsMap)
        {
            var o = new JsonObject();
            foreach (var key in v.AsObject().Keys)
            {
                o[HashRe().Replace(key, "~HASH")] = Normalise(v.AsObject()[key]);
            }

            return o;
        }

        return v;
    }
}
