using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ah.Abstractions;
using Ah.Core.Mux;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah view open --kind tui --session &lt;id&gt;</c>: puts <c>ahd tui</c> in a herdr pane beside the one this process runs in, or
/// focuses the pane it opened for this session before. At most three herdr calls are made, all here, within two seconds in all; a
/// failed call is never retried and no pane is ever closed. The pane id is kept in <c>&lt;state home&gt;/run/view-tui/&lt;session&gt;</c>.
/// Exit codes: 0 opened or focused, 2 a session id that is not a plain name, 3 the TUI did not open.
/// </summary>
internal static class ViewTui
{
    internal const int DidNotOpen = 3;

    internal const int UsageError = 2;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    internal static int Run(CliContext context, string session, TextWriter stdout, TextWriter stderr)
    {
        if (!IsName(session))
        {
            stderr.WriteLine("ah view: --session must be a plain session name");
            return UsageError;
        }

        var here = context.Env.GetVariable("HERDR_PANE_ID");
        if (here is null || !MuxIds.IsHerdrPaneId(here))
        {
            stdout.WriteLine("Run ahd tui in a terminal");
            return DidNotOpen;
        }

        var clock = Stopwatch.StartNew();
        var record = Path.Combine(context.Env.AhHome, "run", "view-tui", session);
        if (ReadRecord(record) is { } known)
        {
            var got = Herdr(context, clock, ["herdr", "pane", "get", known]);
            if (got is { ExitCode: 0 })
            {
                return Focus(context, clock, known, TabOf(Text(got.Stdout)), stdout, stderr);
            }
        }

        var split = Herdr(context, clock, ["herdr", "pane", "split", "--pane", here, "--direction", "right", "--focus"]);
        if (split is not { ExitCode: 0 })
        {
            return Failed(stdout, stderr, "herdr could not split the pane");
        }

        if (PaneIdOf(Text(split.Stdout)) is not { } created)
        {
            return Failed(stdout, stderr, "unexpected herdr output");
        }

        var ahd = AhdPath(context);
        if (ahd is null)
        {
            return Failed(stdout, stderr, "ahd not found");
        }

        var run = Herdr(context, clock, ["herdr", "pane", "run", created, ahd, "tui"]);
        if (run is not { ExitCode: 0 })
        {
            return Failed(stdout, stderr, "herdr could not start ahd tui");
        }

        if (!WriteRecord(record, created))
        {
            stderr.WriteLine("ah view: the pane id could not be kept; the next press opens another pane");
        }

        stdout.WriteLine("TUI opened");
        return 0;
    }

    private static int Focus(CliContext context, Stopwatch clock, string pane, string? tab, TextWriter stdout, TextWriter stderr)
    {
        var focused = Herdr(context, clock, [.. HerdrFocus.AgentFocusArgv(pane)]);
        if (focused is { ExitCode: 0 })
        {
            stdout.WriteLine("TUI focused");
            return 0;
        }

        if (focused is not null && HerdrFocus.ErrorCode(Text(focused.Stdout), Text(focused.Stderr)) == HerdrFocus.AgentNotFound
            && tab is not null && MuxIds.IsHerdrTabId(tab)
            && Herdr(context, clock, [.. HerdrFocus.TabFocusArgv(tab)]) is { ExitCode: 0 })
        {
            stdout.WriteLine("TUI focused");
            return 0;
        }

        return Failed(stdout, stderr, "herdr could not focus the pane");
    }

    private static int Failed(TextWriter stdout, TextWriter stderr, string reason)
    {
        stdout.WriteLine("TUI did not open; run ahd tui in a terminal");
        stderr.WriteLine($"ah view: {reason}");
        return DidNotOpen;
    }

    /// <summary>One herdr call within what is left of the budget; null when the budget is spent or the call did not finish.</summary>
    private static ProcessResult? Herdr(CliContext context, Stopwatch clock, IReadOnlyList<string> argv)
    {
        var left = Budget - clock.Elapsed;
        if (left < TimeSpan.FromMilliseconds(100))
        {
            return null;
        }

        var result = context.Runner.Run(new ProcessRequest(argv, Timeout: left));
        return result.TimedOut || result.ExitCode is null ? null : result;
    }

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    /// <summary>The <c>result.pane</c> object of herdr's JSON answer, <c>{"id":…,"result":{"pane":{…},"type":…}}</c>.</summary>
    private static bool PaneOf(JsonElement root, out JsonElement pane)
    {
        pane = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("pane", out pane) && pane.ValueKind == JsonValueKind.Object;
    }

    /// <summary><c>result.pane.pane_id</c> of herdr's JSON answer when it is a canonical pane id; null for anything else.</summary>
    private static string? PaneIdOf(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return PaneOf(doc.RootElement, out var pane)
                && pane.TryGetProperty("pane_id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } value && MuxIds.IsHerdrPaneId(value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>result.pane.tab_id</c> of herdr's JSON answer, when it is there as a string; null otherwise.</summary>
    private static string? TabOf(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return PaneOf(doc.RootElement, out var pane)
                && pane.TryGetProperty("tab_id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>ahd</c> binary beside the one running, or null when there is none.</summary>
    private static string? AhdPath(CliContext context)
    {
        var self = context.ProcessPath;
        try
        {
            self = File.ResolveLinkTarget(self, returnFinalTarget: true)?.FullName ?? self;
        }
        catch (IOException)
        {
        }

        var sibling = Path.Combine(Path.GetDirectoryName(self) ?? ".", "ahd");
        return File.Exists(sibling) ? sibling : null;
    }

    private static bool IsName(string value) =>
        value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private static string? ReadRecord(string path)
    {
        try
        {
            var line = File.ReadAllText(path).Trim();
            return MuxIds.IsHerdrPaneId(line) ? line : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WriteRecord(string path, string pane)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)) + ".tmp";
            using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                stream.Write(Encoding.ASCII.GetBytes(pane + "\n"));
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
