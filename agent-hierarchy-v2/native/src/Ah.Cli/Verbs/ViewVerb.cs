using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah view open --kind web</c>: asks the daemon to open the browser page, as a press of the band button does. The token the press
/// read from its file arrives as one JSON line, <c>{"token":"…"}</c>, on stdin and never in an argument or the environment. The
/// daemon starts the page and answers whether the browser was handed it; the code and the address stay with the daemon. Exit codes:
/// 0 opened, 2 a usage error or no token on stdin (nothing is sent), 3 the page did not open, 4 the daemon refused the token.
/// </summary>
internal static class ViewVerb
{
    internal const int UsageError = 2;

    internal const int DidNotOpen = 3;

    internal const int Refused = 4;

    private const int TokenChars = 64;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(14);

    internal static int Run(CliContext context, IDaemonClient daemon, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(daemon);
        if (args is ["open", "--kind", "tui", "--session", var session])
        {
            return ViewTui.Run(context, session, stdout, stderr);
        }

        if (args is ["events", ..])
        {
            return Events(daemon, args, stdout, stderr);
        }

        if (args is ["status", "--dir", var dir])
        {
            return Status(context, dir, stdout, stderr);
        }

        if (args.Count != 3 || args[0] != "open" || args[1] != "--kind" || args[2] != "web")
        {
            stderr.WriteLine("ah view: usage: ah view open --kind web  (the token arrives as one JSON line on stdin) | ah view open --kind tui --session <id> | ah view events --sub <id,…> [--since N] [--epoch E] [--wait S] | ah view status --dir <hierarchy dir>");
            return UsageError;
        }

        if (TokenOf(context.ReadStdin ?? ReadProcessStdin) is not { } token)
        {
            stderr.WriteLine("ah view: stdin must hold one JSON line, {\"token\":\"<64 lower-case hex characters>\"}");
            return UsageError;
        }

        var body = Encoding.UTF8.GetBytes(JsonText.Stringify(new JsonObject { ["token"] = token }, 0));
        DaemonReply reply;
        try
        {
            reply = daemon.RequestAsync(new DaemonRequest("POST", "/v1/ui/start", body, Wait, StartIfDown: false)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return Failed(stdout, "the daemon did not answer");
        }

        if (reply.Status == 403)
        {
            stderr.WriteLine("web view can only be opened from the band button");
            return Refused;
        }

        if (reply.Status == 0)
        {
            return Failed(stdout, reply.TimedOut ? "the daemon did not answer in time" : "ahd is not running");
        }

        if (reply.Status != 200)
        {
            return Failed(stdout, MessageOf(reply.Body) ?? $"the daemon answered {reply.Status.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!JsonText.TryParse(reply.Body.Span, out var parsed) || !parsed.IsMap || parsed.AsObject().Get("opened") is not { IsBool: true } opened)
        {
            return Failed(stdout, "the daemon's answer could not be read");
        }

        if (!opened.AsBool())
        {
            return Failed(stdout, "no browser");
        }

        stdout.WriteLine("Web opened");
        return 0;
    }

    /// <summary>
    /// <c>ah view status --dir &lt;H&gt;</c> (files-off-disk F3a): the <c>status.read</c> document of hierarchy dir H, computed in
    /// process from the database, as one JSON line: the document <c>/v1/status</c> serves. Never asks or starts ahd. Exit 2 when the dir
    /// is not absolute, 1 when the read fails.
    /// </summary>
    private static int Status(CliContext context, string dir, TextWriter stdout, TextWriter stderr)
    {
        if (!dir.StartsWith('/'))
        {
            stderr.WriteLine("ah view status: --dir must be an absolute hierarchy dir");
            return UsageError;
        }

        var g = context.Services!.Value;
        try
        {
            var doc = new Composition.StateServiceReader(g.InternalStateRead)
                .Read("status", "read", dir, new JsonObject { ["dir"] = dir });
            stdout.WriteLine(JsonText.Stringify(doc, 0));
            return 0;
        }
        catch (InvalidOperationException e)
        {
            stderr.WriteLine($"ah view status: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// <c>ah view events --sub &lt;id,…&gt; [--since N] [--epoch E] [--wait S]</c>: one poll of the slice feed, printing the daemon's body as
    /// it came. It never starts ahd: no daemon, or any answer but 200, is exit 1 with one line on stderr.
    /// </summary>
    private const int MaxWaitSeconds = 600;

    private static int Events(IDaemonClient daemon, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        string? sub = null;
        string? epoch = null;
        long since = 0;
        var wait = 0;
        var ok = args.Count % 2 == 1;
        for (var i = 1; ok && i + 1 < args.Count; i += 2)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--sub":
                    sub = value;
                    break;
                case "--epoch":
                    epoch = value;
                    break;
                case "--since":
                    ok = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out since);
                    break;
                case "--wait":
                    ok = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out wait) && wait <= MaxWaitSeconds;
                    break;
                default:
                    ok = false;
                    break;
            }
        }

        var ids = sub?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (!ok || ids.Length == 0)
        {
            stderr.WriteLine("ah view: usage: ah view events --sub <id,…> [--since N] [--epoch E] [--wait S]");
            return UsageError;
        }

        DaemonReply reply;
        try
        {
            var query = Ah.DaemonClient.DaemonClientModule.SliceEventsQuery(ids, [], since, epoch, wait);
            reply = daemon.RequestAsync(new DaemonRequest("GET", "/v1/view?path=%2Fevents" + query, default, TimeSpan.FromSeconds(wait + 15), StartIfDown: false, AnyVersion: true)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            stderr.WriteLine("ah view events: the daemon did not answer");
            return 1;
        }

        if (reply.Status != 200)
        {
            stderr.WriteLine(reply.Status == 0
                ? $"ah view events: {(reply.TimedOut ? "the daemon did not answer in time" : "ahd is not running")}"
                : $"ah view events: the daemon answered {reply.Status.ToString(CultureInfo.InvariantCulture)}{(MessageOf(reply.Body) is { } m ? ": " + m : string.Empty)}");
            return 1;
        }

        stdout.WriteLine(Encoding.UTF8.GetString(reply.Body.Span));
        return 0;
    }

    private static int Failed(TextWriter stdout, string reason)
    {
        stdout.WriteLine($"Web did not open ({reason}); run `ahd ui start` in a terminal");
        return DidNotOpen;
    }

    private const int StdinCap = 4096;

    private static readonly TimeSpan StdinWait = TimeSpan.FromSeconds(1);

    private static ReadOnlyMemory<byte> ReadProcessStdin() =>
        Console.IsInputRedirected ? ReadBounded(Console.OpenStandardInput(), StdinCap, StdinWait) : ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Reads at most <paramref name="cap"/> bytes, stopping at the first newline or closing brace, at end of input, or when
    /// <paramref name="wait"/> runs out. The token arrives as one JSON line, so a caller that never closes stdin still ends the
    /// read; a read that times out or fails yields nothing.
    /// </summary>
    internal static ReadOnlyMemory<byte> ReadBounded(Stream input, int cap, TimeSpan wait)
    {
        var buffer = new byte[cap];
        var length = 0;
        var reading = Task.Run(() =>
        {
            while (length < cap)
            {
                var got = input.Read(buffer, length, cap - length);
                if (got <= 0)
                {
                    return;
                }

                var ended = buffer.AsSpan(length, got).IndexOfAny((byte)'\n', (byte)'}') >= 0;
                length += got;
                if (ended)
                {
                    return;
                }
            }
        });
        try
        {
            return reading.Wait(wait) ? buffer.AsMemory(0, length) : ReadOnlyMemory<byte>.Empty;
        }
        catch (AggregateException)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>The token on stdin: the object of one JSON line with a <c>token</c> of 64 lower-case hex characters; null for anything else.</summary>
    private static string? TokenOf(Func<ReadOnlyMemory<byte>>? readStdin)
    {
        if (readStdin is null)
        {
            return null;
        }

        var bytes = readStdin();
        if (bytes.IsEmpty || !JsonText.TryParse(bytes.Span, out var parsed) || !parsed.IsMap
            || parsed.AsObject().Get("token") is not { IsText: true } text || text.AsString() is not { Length: TokenChars } token
            || !token.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return null;
        }

        return token;
    }

    private static string? MessageOf(ReadOnlyMemory<byte> body) =>
        JsonText.TryParse(body.Span, out var parsed) && parsed.IsMap && parsed.AsObject().Get("error") is { IsMap: true } error
        && error.AsObject().Get("message") is { IsText: true } message && message.AsString().Length > 0 && message.AsString().All(static c => !char.IsControl(c))
            ? message.AsString()
            : null;
}
