using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>
/// One paste: <paramref name="Member"/> is the recipient's team row, <paramref name="Entry"/> the mailbox entry the payload carries a
/// marker of (null for an explicit deliver), <paramref name="Activity"/> the member's recorded activity (<c>working</c> blocks),
/// <paramref name="Wait"/> adds <c>--wait</c> to herdr's prompt call, <paramref name="Source"/> names the caller in the attempt row,
/// <paramref name="LockWait"/> caps how long the per-member lock is waited for (null: <c>AH_PASTE_LOCK_WAIT_MS</c>, 15 s).
/// </summary>
public sealed record PasteOptions(
    JsonValue Member,
    string TeamName,
    string? TeamTransport,
    string Payload,
    PasteEntry? Entry = null,
    string? Activity = null,
    bool Wait = false,
    string Source = "ladder",
    int MaxChars = PasteOptions.DefaultMaxChars,
    TimeSpan? LockWait = null,
    string? HerdrFlavour = null)
{
    /// <summary>The longest payload, in characters, a paste carries unless the caller says otherwise.</summary>
    public const int DefaultMaxChars = 500;
}

/// <summary>
/// The one hardened paste path. A message is typed into a member's composer only when the screen shows an idle, empty composer, by the
/// transport the member is recorded on (herdr: its own prompt call; tmux: bracketed paste into the member's pane). Nothing is typed
/// over a draft, a running turn, a prompt or an unrecognised screen. The only key ever pressed is one Enter on a composer that holds
/// exactly the staged payload, and an unconfirmed paste is never retried. Every attempt appends one row to the attempt log.
/// </summary>
public interface IPaste
{
    /// <summary>The longest payload a paste carries.</summary>
    int MaxChars { get; }

    /// <summary>
    /// Where a member runs, from its recorded placement alone: a row with <c>source</c> is on herdr, otherwise the team's transport; the
    /// pane is its <c>transport_id</c>. <c>{pasteable: false, reason: "no-pane"}</c> when no multiplexer holds it. The caller's own
    /// environment is never consulted.
    /// </summary>
    JsonObject PlacementOf(JsonValue member, string? teamTransport);

    /// <summary>
    /// Pastes <see cref="PasteOptions.Payload"/> (one line, at most <see cref="PasteOptions.MaxChars"/> characters) into the member:
    /// <c>{transport, pane, outcome: confirmed | unconfirmed | refused, reason, ...}</c>.
    /// </summary>
    JsonObject Paste(PasteOptions options);
}

/// <summary>
/// The paste path. <paramref name="PasteLocks"/> must judge a lock stale after 60 seconds. Every herdr call goes through <paramref name="Herdr"/>.
/// </summary>
public sealed record PasteService(
    IAhEnvironment Environment,
    IFileReader Files,
    IRawFileWriter Writer,
    IClock Clock,
    IProcessRunner Processes,
    IConfigCore Config,
    IScreenRules Screens,
    IKindHarnesses Harnesses,
    IMailbox Mailbox,
    ILockProvider PasteLocks,
    ISleeper Sleeper,
    int OwnPid,
    IHerdr Herdr) : IPaste
{
    /// <summary>How old a member's paste lock must be before another paste takes it over.</summary>
    public static readonly TimeSpan LockStaleAfter = TimeSpan.FromSeconds(60);

    private const int CallTimeoutMs = 15000;

    public int MaxChars => 500;

    public JsonObject PlacementOf(JsonValue member, string? teamTransport)
    {
        var transport = member.IsTruthy && member.Member("source") is { IsTruthy: true } ? "herdr" : teamTransport;
        var pane = member.Member("transport_id") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
        if (pane is not null && (transport == "tmux" ? !LaunchModule.IsTmuxPaneId(pane) : transport == "herdr" && !LaunchModule.IsHerdrPaneId(pane)))
        {
            pane = null;
        }

        if ((transport is not ("herdr" or "tmux")) || pane is null)
        {
            return new JsonObject
            {
                ["pasteable"] = false,
                ["reason"] = "no-pane",
                ["transport"] = transport is not null && transport.Length > 0 ? transport : JsonValue.Null,
                ["pane"] = pane is not null ? pane : JsonValue.Null,
            };
        }

        return new JsonObject { ["pasteable"] = true, ["transport"] = transport, ["pane"] = pane };
    }

    public JsonObject Paste(PasteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // ah2-180 B4: a pane on another herdr than the daemon's is typed into through that herdr's adapter, as Teardown.HerdrOf chooses it.
        if (options.HerdrFlavour is { } flavour && flavour != Herdr.Flavour && Herdr is global::Ah.Launch.Herdr own)
        {
            return (this with { Herdr = own.ForFlavour(flavour) }).Paste(options with { HerdrFlavour = null });
        }

        var member = options.Member;
        var place = PlacementOf(member, options.TeamTransport);
        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var memberName = member.Member("name") is { } n ? JsConvert.ToJsString(n) : "undefined";
        var transportValue = place.Get("transport") ?? JsonValue.Null;
        var paneValue = place.Get("pane") ?? JsonValue.Null;

        JsonObject Finish(JsonObject res)
        {
            var output = new JsonObject { ["transport"] = transportValue, ["pane"] = paneValue };
            foreach (var key in res.Keys)
            {
                output[key] = res[key];
            }

            var row = new JsonObject
            {
                ["source"] = options.Source,
                ["team"] = options.TeamName,
                ["member"] = memberName,
                ["transport"] = output.Get("transport") ?? JsonValue.Null,
                ["pane"] = output.Get("pane") ?? JsonValue.Null,
                ["outcome"] = output.Get("outcome") ?? JsonValue.Null,
            };
            if (output.Get("reason") is { IsTruthy: true } reason)
            {
                row["reason"] = reason;
            }

            if (output.Get("why") is { IsTruthy: true } whyText)
            {
                row["why"] = whyText;
            }

            row["readiness"] = output.Get("readiness") is { IsTruthy: true } readiness
                ? readiness
                : output.Get("outcome") is { IsText: true } outcome && outcome.AsString() == "refused" ? output.Get("reason") ?? JsonValue.Null : "ready";
            Mailbox.LogPasteAttempt(options.Entry, row);
            return output;
        }

        JsonObject Refuse(string reason, JsonObject? extra = null)
        {
            var res = new JsonObject { ["outcome"] = "refused", ["reason"] = reason };
            if (extra is not null)
            {
                foreach (var key in extra.Keys)
                {
                    res[key] = extra[key];
                }
            }

            return Finish(res);
        }

        var payload = options.Payload;
        if (payload.Length == 0 || payload.Contains('\r', StringComparison.Ordinal) || payload.Contains('\n', StringComparison.Ordinal) || payload.Length > options.MaxChars)
        {
            return Refuse("bad_request", new JsonObject { ["chars"] = payload.Length });
        }

        if (member.Member("paste") is { IsBool: true } pasteFlag && !pasteFlag.AsBool())
        {
            return Refuse("paste-disabled");
        }

        if (place.Get("pasteable") is not { IsTruthy: true })
        {
            return Refuse(JsConvert.ToJsString(place.Get("reason")));
        }

        var transport = JsConvert.ToJsString(transportValue);
        var pane = JsConvert.ToJsString(paneValue);

        if (!LaunchModule.IsLockNamePart(options.TeamName) || !LaunchModule.IsLockNamePart(memberName))
        {
            return Refuse("bad_request", new JsonObject { ["field"] = "name" });
        }

        var lockFile = NodePath.Join(Environment.AhHome, "run", "paste", $"{options.TeamName}-{memberName}.lock");
        MakeDirectories(NodePath.Dirname(lockFile));
        var wait = options.LockWait ?? TimeSpan.FromMilliseconds(Num("AH_PASTE_LOCK_WAIT_MS", 15000));
        using var held = PasteLocks.Acquire(lockFile, wait);
        if (held is null)
        {
            return Refuse("lock-busy");
        }

        // herdr finds a member by its name; a session with no member name is found by its own pane.
        var target = member.Member("target") is { IsText: true } named && named.AsString().Length > 0 ? named.AsString() : memberName;
        var surface = ReadSurface(transport, pane, target);

        // A fresh pane is found before herdr gives it an agent_status (b353): re-read it briefly, and a real status then decides.
        // Still none: nothing is typed, and the entry stays in the mailbox it was sent to.
        var statusBy = Clock.UtcNow.AddMilliseconds(Num("AH_PASTE_STATUS_WAIT_MS", 3000));
        while (surface.Statusless && Clock.UtcNow < statusBy)
        {
            Sleeper.Sleep(TimeSpan.FromMilliseconds(Num("AH_PASTE_POLL_MS", 200)));
            surface = ReadSurface(transport, pane, target);
        }

        if (surface.Statusless)
        {
            return Finish(new JsonObject { ["outcome"] = "mailed", ["reason"] = "no-status", ["readiness"] = "no-status" });
        }

        var ready = Readiness(kind, surface, options.Activity);
        if (ready.Get("ready") is not { IsTruthy: true })
        {
            var rest = new JsonObject { ["readiness"] = ready.Get("reason") ?? JsonValue.Null };
            foreach (var key in ready.Keys)
            {
                if (key == "seen")
                {
                    continue;
                }

                rest[key] = ready[key];
            }

            return Refuse(JsConvert.ToJsString(ready.Get("reason")), rest);
        }

        var verifyMs = Num("AH_PASTE_VERIFY_MS", 5000);
        var pollMs = Num("AH_PASTE_POLL_MS", 200);
        var deadline = Clock.UtcNow.AddMilliseconds(verifyMs);

        bool Acked() => options.Entry is { Box: { Length: > 0 } box } entry && Mailbox.IsAcked(box, entry.Id);

        JsonObject Confirmed(string by)
        {
            var ack = Mailbox.AckPaste(options.Entry) switch
            {
                PasteAck.Already => "already",
                PasteAck.Paste => "paste",
                _ => "failed",
            };
            return Finish(new JsonObject { ["outcome"] = "confirmed", ["reason"] = by, ["ack"] = ack, ["readiness"] = "ready" });
        }

        if (transport == "herdr")
        {
            var typeArgs = new List<string> { "agent", "prompt", target, payload };
            if (options.Wait)
            {
                typeArgs.Add("--wait");
            }

            var typed = Run("herdr", typeArgs);
            if (!typed.Ok)
            {
                var why = JsText.Trim(typed.Stderr.Length > 0 ? typed.Stderr : typed.Stdout);
                return Finish(new JsonObject
                {
                    ["outcome"] = "unconfirmed",
                    ["reason"] = "type-failed",
                    ["why"] = why.Length > 200 ? why[..200] : why,
                    ["readiness"] = "ready",
                });
            }

            var retried = false;
            while (true)
            {
                if (Acked())
                {
                    return Confirmed("hook-ack");
                }

                var s = ReadSurface(transport, pane, target);
                if (s.Ok && s.Status is "working" or "blocked")
                {
                    return Confirmed("turn-started");
                }

                if (s.Ok && !retried)
                {
                    var seen = Screens.RecognizeScreen(kind, s.Screen, s.Status);
                    var text = ComposerText(kind, s.Screen, seen);
                    if (text is not null && Squash(text) == Squash(payload))
                    {
                        Run("herdr", ["agent", "send-keys", target, "Enter"]);
                        retried = true;
                    }
                }

                if (Clock.UtcNow >= deadline)
                {
                    break;
                }

                Sleeper.Sleep(TimeSpan.FromMilliseconds(pollMs));
            }

            if (options.Wait)
            {
                return Confirmed("prompt-returned");
            }

            return Finish(new JsonObject { ["outcome"] = "unconfirmed", ["reason"] = "no-activity", ["entered"] = retried, ["readiness"] = "ready" });
        }

        var staged = StageTmux(pane, payload);
        if (staged is not null)
        {
            return Finish(new JsonObject { ["outcome"] = "unconfirmed", ["reason"] = "type-failed", ["why"] = staged, ["readiness"] = "ready" });
        }

        var want = Squash(payload);
        var entered = false;
        var reEntered = false;
        var enteredAt = DateTimeOffset.MinValue;
        while (true)
        {
            if (Acked())
            {
                return Confirmed("hook-ack");
            }

            var s = ReadSurface(transport, pane, memberName);
            if (s.Ok)
            {
                var seen = Screens.RecognizeScreen(kind, s.Screen, s.Status);
                var text = ComposerText(kind, s.Screen, seen);
                var squashed = text is not null ? Squash(text) : null;
                var busy = seen.Get("busy") is { IsTruthy: true };
                var draft = seen.Get("draft") is { IsTruthy: true };
                var prompt = seen.Get("prompt") is { IsTruthy: true };
                if (!entered && squashed is not null && squashed == want)
                {
                    Run("tmux", ["send-keys", "-t", pane, "Enter"]);
                    entered = true;
                    enteredAt = Clock.UtcNow;
                }
                else if (entered && busy && !draft && !prompt)
                {
                    return Confirmed("turn-started");
                }
                else if (entered && (prompt || squashed == string.Empty))
                {
                    return Confirmed("turn-started");
                }
                else if (entered && !reEntered && !busy && squashed == want && Clock.UtcNow >= enteredAt.AddSeconds(1))
                {
                    Run("tmux", ["send-keys", "-t", pane, "Enter"]);
                    reEntered = true;
                }
                else if (!entered && squashed is not null && squashed != want && squashed != string.Empty && !want.StartsWith(squashed, StringComparison.Ordinal))
                {
                    return Finish(new JsonObject { ["outcome"] = "unconfirmed", ["reason"] = "composer-changed", ["readiness"] = "ready" });
                }
            }

            if (Clock.UtcNow >= deadline)
            {
                break;
            }

            Sleeper.Sleep(TimeSpan.FromMilliseconds(pollMs));
        }

        return Finish(new JsonObject
        {
            ["outcome"] = "unconfirmed",
            ["reason"] = entered ? "no-activity" : "not-staged",
            ["entered"] = entered,
            ["readiness"] = "ready",
        });
    }

    private sealed record Surface(bool Ok, string Screen, string? Status, bool NotFound, bool Statusless = false);

    private sealed record RunResult(bool Ok, string Stdout, string Stderr, int? Status);

    private RunResult Run(string command, List<string> args)
    {
        if (command == "herdr")
        {
            var h = Herdr.Run(args, CallTimeoutMs);
            return new RunResult(h.Ok, h.Stdout, h.Stderr, h.Status);
        }

        var argv = new List<string>(args.Count + 1) { command };
        argv.AddRange(args);
        var r = Processes.Run(new ProcessRequest(argv, null, null, default, TimeSpan.FromMilliseconds(CallTimeoutMs), 1 << 20));
        var spawnFailed = r.ExitCode == 127 && r.Stdout.Length == 0 && r.Stderr.Length == 0;
        return new RunResult(
            r is { ExitCode: 0, TimedOut: false, Truncated: false } && !spawnFailed,
            Utf8Text.Decode(r.Stdout.Span),
            Utf8Text.Decode(r.Stderr.Span),
            r.ExitCode);
    }

    /// <summary>The member's visible screen and, where the transport reports one, its agent status.</summary>
    private Surface ReadSurface(string transport, string pane, string memberName)
    {
        if (transport == "tmux")
        {
            var captured = Run("tmux", ["capture-pane", "-p", "-t", pane]);
            return new Surface(captured.Ok, captured.Ok ? captured.Stdout : string.Empty, null, false);
        }

        var got = Run("herdr", ["agent", "get", memberName]);
        string? status = null;
        var notFound = false;
        var found = false;
        if (JsonText.TryParse(Utf8Text.Encode(got.Stdout), out var doc))
        {
            found = got.Ok && doc.Member("result")?.Member("agent") is { IsMap: true };
            if (doc.Member("result")?.Member("agent")?.Member("agent_status") is { IsText: true } s)
            {
                status = s.AsString();
            }

            notFound = doc.Member("error") is { IsTruthy: true } error && error.Member("code") is { IsText: true } code && code.AsString() == "agent_not_found";
        }

        var read = Run("herdr", ["agent", "read", memberName, "--source", "visible"]);
        // Only idle/done, working or blocked is a status to judge by; none, or `unknown`, is re-read and then left to the mailbox.
        var statusless = found && status is not ("idle" or "done" or "working" or "blocked");
        return new Surface(read.Ok && status is not null, read.Ok ? read.Stdout : string.Empty, status, notFound, statusless);
    }

    /// <summary>The text in a Codex composer row, or null when the screen does not end in its composer shape.</summary>
    private string? CodexComposerText(string? screen)
    {
        var lines = (screen ?? string.Empty).Split('\n').ToList();
        while (lines.Count > 0 && JsText.Trim(lines[^1]).Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 4)
        {
            return null;
        }

        var row = new StringBuilder(lines[^3].Length);
        foreach (var c in lines[^3])
        {
            row.Append(c is >= '⠀' and <= '⣿' ? ' ' : c);
        }

        var text = row.ToString();
        var glyphs = Harnesses.ComposerGlyphs("codex");
        if (text.Length >= 2 && glyphs.Contains(text[0].ToString()) && text[1] == ' ')
        {
            return JsText.Trim(text[2..]);
        }

        return null;
    }

    /// <summary>
    /// A narrow pane wraps the composer at word boundaries and splits a token longer than the pane at the column, so the rows rejoin to
    /// the payload only once all whitespace is dropped from both sides.
    /// </summary>
    private static string Squash(string? s)
    {
        var sb = new StringBuilder();
        foreach (var c in s ?? string.Empty)
        {
            if (!JsText.IsSpace(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private string? ComposerText(string kind, string screen, JsonObject seen)
    {
        if (kind == "codex")
        {
            return CodexComposerText(screen);
        }

        return seen.Get("recognized") is { IsTruthy: true } && seen.Get("prompt") is not { IsTruthy: true }
            ? JsConvert.ToJsString(seen.Get("text") ?? string.Empty)
            : null;
    }

    private JsonObject Readiness(string kind, Surface surface, string? activity)
    {
        if (!surface.Ok)
        {
            return new JsonObject
            {
                ["ready"] = false,
                ["reason"] = surface.NotFound ? "not-live" : "indeterminate",
                ["why"] = "the member's screen or status could not be read",
            };
        }

        var seen = Screens.RecognizeScreen(kind, surface.Screen, surface.Status);
        var hash = Screens.ScreenHash(surface.Screen);
        if (seen.Get("prompt") is { IsTruthy: true } prompt)
        {
            return new JsonObject
            {
                ["ready"] = false,
                ["reason"] = "blocked",
                ["prompt"] = prompt,
                ["block"] = seen.Get("block") ?? new JsonArray(),
                ["screen_hash"] = hash,
                ["seen"] = seen,
            };
        }

        if (surface.Status == "blocked")
        {
            return new JsonObject
            {
                ["ready"] = false,
                ["reason"] = "blocked",
                ["prompt"] = "harness-prompt",
                ["block"] = new JsonArray(),
                ["screen_hash"] = hash,
                ["seen"] = seen,
            };
        }

        if (surface.Status == "working" || activity == "working" || seen.Get("busy") is { IsTruthy: true } || seen.Get("draft") is { IsTruthy: true })
        {
            return new JsonObject { ["ready"] = false, ["reason"] = "busy", ["seen"] = seen };
        }

        if (seen.Get("composer") is not { IsTruthy: true })
        {
            return new JsonObject
            {
                ["ready"] = false,
                ["reason"] = "indeterminate",
                ["why"] = "the screen is not a recognised idle, empty composer",
                ["seen"] = seen,
            };
        }

        return new JsonObject { ["ready"] = true, ["seen"] = seen };
    }

    /// <summary>Stages <paramref name="payload"/> in the pane by bracketed paste; null on success, else why not. Nothing is submitted.</summary>
    private string? StageTmux(string pane, string payload)
    {
        var dir = NodePath.Join(Environment.AhHome, "run");
        MakeDirectories(dir);
        var buffer = $"ah-paste-{OwnPid.ToString(CultureInfo.InvariantCulture)}-{Clock.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        var file = NodePath.Join(dir, buffer + ".txt");
        Writer.CreateExclusive(file, Utf8Text.Encode(payload), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var load = Run("tmux", ["load-buffer", "-b", buffer, file]);
            if (!load.Ok)
            {
                var detail = JsText.Trim(load.Stderr);
                return $"tmux load-buffer failed: {(detail.Length > 0 ? detail : $"exit {(load.Status is { } st ? st.ToString(CultureInfo.InvariantCulture) : "null")}")}";
            }

            var paste = Run("tmux", ["paste-buffer", "-d", "-r", "-p", "-b", buffer, "-t", pane]);
            if (!paste.Ok)
            {
                var detail = JsText.Trim(paste.Stderr);
                return $"tmux paste-buffer failed: {(detail.Length > 0 ? detail : $"exit {(paste.Status is { } st ? st.ToString(CultureInfo.InvariantCulture) : "null")}")}";
            }

            return null;
        }
        finally
        {
            Writer.Unlink(file);
        }
    }

    private double Num(string name, double fallback)
    {
        var text = Environment.GetVariable(name);
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        var v = JsConvert.ToNumber(JsonValue.FromString(text));
        return double.IsFinite(v) && v >= 0 ? v : fallback;
    }

    private void MakeDirectories(string path)
    {
        if (path.Length == 0 || path == "/" || Files.Exists(path))
        {
            return;
        }

        MakeDirectories(NodePath.Dirname(path));
        Writer.Mkdir(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
