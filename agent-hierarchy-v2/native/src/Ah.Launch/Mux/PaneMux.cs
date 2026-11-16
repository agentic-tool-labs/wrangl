using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>A pane's current screen as its multiplexer prints it, escape sequences kept; the cursor is null where the multiplexer does not say.</summary>
public sealed record PaneScreen(int Cols, int Rows, byte[] Ansi, int? CursorX, int? CursorY);

/// <summary>One read: the screen, or the reason there is none (<c>pane closed</c> when the pane no longer exists).</summary>
public sealed record PaneRead(PaneScreen? Screen, string? Reason);

/// <summary>
/// A pane in a multiplexer's own listing: its transport, the instance (tmux server socket or herdr socket) that lists it, its id
/// there, the pid of the process the pane started (null when not asked for), and its herdr tab id (null for tmux).
/// </summary>
public sealed record MuxPane(string Transport, string Instance, string PaneId, int? RootPid, string? TabId);

/// <summary>
/// What identifies a pane at one moment: the pid of the process it started and, for tmux, the pid and start time (seconds since the
/// epoch, as tmux prints it) of the server that holds it. herdr has no server fields.
/// </summary>
public sealed record PaneIdentity(int RootPid, int? ServerPid, long? ServerStart);

/// <summary>
/// The panes of one transport, or the reason no usable listing could be had. <paramref name="Answered"/> is false when no
/// multiplexer instance answered, or when any instance did not: a pane missing from the list may then be in the one that did not answer.
/// <paramref name="Truncated"/> is true when the listing went past the instance or pane bounds and holds only what was read before them:
/// a pane in it is there, a pane missing from it is unknown.
/// </summary>
public sealed record PaneListing(IReadOnlyList<MuxPane> Panes, string? Failure, bool Answered = true, bool Truncated = false);

/// <summary>Reads panes and lists them, always through the multiplexer's own commands and never through a shell.</summary>
public interface IPaneMux
{
    /// <summary>
    /// The visible screen of <paramref name="paneId"/> on <paramref name="transport"/> (<c>tmux</c> or <c>herdr</c>) in
    /// <paramref name="instance"/>, which must be one of <paramref name="listed"/>, the instances the daemon's own listing
    /// has. An id that is not canonical, or an instance that is not a listed one, is refused without running anything.
    /// </summary>
    PaneRead Read(string transport, string paneId, string? instance, IReadOnlyCollection<string> listed);

    /// <summary>
    /// Every pane of every instance of <paramref name="transport"/> the user has, each instance asked for its own listing. With
    /// <paramref name="withPids"/> each pane also carries its root pid, or none when its process could not be read (the pane is still
    /// listed). An instance that is not running is skipped; the listing has a
    /// <see cref="PaneListing.Failure"/> when one could not be had at all or went past the bounds.
    /// </summary>
    PaneListing Enumerate(string transport, bool withPids);

    /// <summary>
    /// The last <paramref name="lines"/> lines of <paramref name="paneId"/> as plain text with wrapped lines joined, read from the one instance
    /// the daemon's own listing has it in. Null when no one instance lists it, the id is not canonical, or the read failed.
    /// </summary>
    string? ReadRecent(string transport, string paneId, int lines) => null;
}

/// <summary>
/// The reader over <paramref name="Mux"/>. tmux keeps its sockets in <c>${TMUX_TMPDIR:-/tmp}/tmux-&lt;uid&gt;</c>, a directory owned by
/// <paramref name="Uid"/>, with <paramref name="TmuxTmpdir"/> the variable's value (null: unset); <paramref name="HerdrDefaultSocket"/> is herdr's default socket, listed when it is there.
/// <paramref name="HerdrEnvironment"/> gives the environment of a herdr call for a pinned instance socket (null: <c>herdr session
/// list</c>, which pins none): the inherited herdr pane variables are never part of it.
/// </summary>
public sealed partial record PaneMux(
    IMuxRunner Mux,
    IFileReader Files,
    string? TmuxTmpdir,
    uint Uid,
    string? HerdrDefaultSocket,
    Func<string?, IReadOnlyDictionary<string, string?>> HerdrEnvironment,
    IHerdr HerdrCalls) : IPaneMux
{
    private string TmuxDirectory => MuxIds.TmuxSocketDirectory(TmuxTmpdir, (int)Uid);

    public const string ClosedReason = "pane closed";

    public const string UnreadableReason = "pane unreadable";

    public const string NotCanonicalReason = "pane id is not canonical";

    public const string UnknownInstanceReason = "multiplexer instance is not one this daemon lists";

    /// <summary>The most herdr instances, and the most panes in one, a listing goes through.</summary>
    public const int MaxInstances = 8;

    public const int MaxPanesPerInstance = 256;

    private const int CallTimeoutMs = 2000;

    private const int ScreenCapBytes = 1 << 20;

    private static bool IsPaneId(string transport, string id) =>
        transport switch
        {
            "tmux" => MuxIds.IsTmuxPaneId(id),
            "herdr" => MuxIds.IsHerdrPaneId(id),
            _ => false,
        };

    public PaneRead Read(string transport, string paneId, string? instance, IReadOnlyCollection<string> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(paneId);
        if (!IsPaneId(transport, paneId))
        {
            return new PaneRead(null, NotCanonicalReason);
        }

        if (instance is null || !(transport == "tmux" ? MuxIds.IsTmuxInstance(instance, TmuxTmpdir, (int)Uid, listed, Files) : MuxIds.IsHerdrInstance(instance, listed)))
        {
            return new PaneRead(null, UnknownInstanceReason);
        }

        return transport == "tmux" ? ReadTmux(paneId, instance) : ReadHerdr(paneId, instance);
    }

    public string? ReadRecent(string transport, string paneId, int lines)
    {
        var listing = Enumerate(transport, withPids: false);
        if (listing.Failure is not null || !IsPaneId(transport, paneId) || listing.Panes.Where(p => p.PaneId == paneId).ToList() is not [var pane])
        {
            return null;
        }

        var count = lines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shot = transport == "tmux"
            ? Mux.Run("tmux", ["-S", pane.Instance, "capture-pane", "-p", "-J", "-S", "-" + count, "-t", paneId], CallTimeoutMs, ScreenCapBytes)
            : Herdr(pane.Instance, ["pane", "read", paneId, "--source", "recent-unwrapped", "--lines", count], ScreenCapBytes);
        return shot.Ok ? shot.Stdout : null;
    }

    private PaneRead ReadTmux(string paneId, string instance)
    {
        // The screen is read first: tmux answers a pane that is gone with "can't find pane" and a failure here, where display-message
        // prints nothing and succeeds.
        var shot = Mux.Run("tmux", ["-S", instance, "capture-pane", "-e", "-p", "-t", paneId], CallTimeoutMs, ScreenCapBytes);
        if (!shot.Ok)
        {
            return new PaneRead(null, Failure(shot));
        }

        var size = Mux.Run("tmux", ["-S", instance, "display-message", "-p", "-t", paneId, "#{pane_width} #{pane_height} #{cursor_x} #{cursor_y}"], CallTimeoutMs);
        if (!size.Ok)
        {
            return new PaneRead(null, Failure(size));
        }

        var text = size.Stdout.Trim();
        if (text.Length == 0)
        {
            return new PaneRead(null, ClosedReason);
        }

        var parts = text.Split(' ');
        return parts.Length == 4 && TryInt(parts[0], out var cols) && TryInt(parts[1], out var rows) && TryInt(parts[2], out var x) && TryInt(parts[3], out var y) && cols >= 1 && rows >= 1
            ? new PaneRead(new PaneScreen(cols, rows, Utf8Text.Encode(shot.Stdout), x, y), null)
            : new PaneRead(null, UnreadableReason);
    }

    private PaneRead ReadHerdr(string paneId, string instance)
    {
        var shot = Herdr(instance, ["pane", "read", paneId, "--source", "visible", "--ansi"], ScreenCapBytes);
        if (!shot.Ok)
        {
            return new PaneRead(null, Failure(shot));
        }

        // No size or cursor is read beside the text, so the size is the text's own and there is no cursor.
        var lines = shot.Stdout.TrimEnd('\n').Split('\n');
        var cols = Math.Max(1, lines.Max(l => CellCount(l)));
        return new PaneRead(new PaneScreen(cols, Math.Max(1, lines.Length), Utf8Text.Encode(shot.Stdout), null, null), null);
    }

    /// <summary>
    /// What identifies one pane right now, asked of <paramref name="instance"/> alone and never from a cached listing: the pid of
    /// the process the pane started and, for tmux, the pid and start time of the server (a restarted server keeps its socket path
    /// and may reuse a pane id). tmux answers all three in one <c>display-message</c>; herdr asks for that pane's process info.
    /// Null when the pane is not there, the instance is not one of <paramref name="listed"/> (the daemon's own listing), the id is
    /// not canonical, or the call was not answered.
    /// </summary>
    public PaneIdentity? IdentityOf(string transport, string paneId, string instance, IReadOnlyCollection<string> listed)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(paneId);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(listed);
        if (!IsPaneId(transport, paneId)
            || !(transport == "tmux" ? MuxIds.IsTmuxInstance(instance, TmuxTmpdir, (int)Uid, listed, Files) : MuxIds.IsHerdrInstance(instance, listed)))
        {
            return null;
        }

        if (transport == "tmux")
        {
            var asked = Mux.Run("tmux", ["-S", instance, "display-message", "-p", "-t", paneId, "#{pane_pid} #{pid} #{start_time}"], CallTimeoutMs);
            if (!asked.Ok)
            {
                return null;
            }

            var parts = asked.Stdout.Trim().Split(' ');
            return parts.Length == 3 && TryInt(parts[0], out var root) && root > 1 && TryInt(parts[1], out var server) && server > 1
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started)
                    ? new PaneIdentity(root, server, started)
                    : null;
        }

        var info = Herdr(instance, ["pane", "process-info", "--pane", paneId], ScreenCapBytes);
        return info.Ok && JsonText.TryParse(Utf8Text.Encode(info.Stdout), out var detail) && ShellPid(detail) is { } shell ? new PaneIdentity(shell, null, null) : null;
    }

    public PaneListing Enumerate(string transport, bool withPids)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return transport switch
        {
            "tmux" => EnumerateTmux(),
            "herdr" => EnumerateHerdr(withPids),
            _ => new PaneListing([], $"no panes are listed for transport {transport}"),
        };
    }

    private PaneListing EnumerateTmux()
    {
        var found = new List<MuxPane>();
        if (Files.Stat(TmuxDirectory) is not { Kind: FileKind.Directory } directory || directory.Uid != Uid)
        {
            return new PaneListing(found, null);
        }

        foreach (var name in Files.List(TmuxDirectory).Select(Path.GetFileName).Where(n => n is { Length: > 0 }).Order(StringComparer.Ordinal))
        {
            var socket = TmuxDirectory + "/" + name;
            if (!IsListedSocketPath(socket) || !IsOwnSocket(socket))
            {
                continue;
            }

            var listing = Mux.Run("tmux", ["-S", socket, "list-panes", "-a", "-F", "#{pane_id} #{pane_pid}"], CallTimeoutMs);
            if (Unanswered(listing))
            {
                return new PaneListing([], "tmux pane enumeration failed");
            }

            if (!listing.Ok)
            {
                continue;
            }

            foreach (var line in listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && MuxIds.IsTmuxPaneId(parts[0]) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 1)
                {
                    found.Add(new MuxPane("tmux", socket, parts[0], pid, null));
                }
            }
        }

        return new PaneListing(found, null);
    }

    private PaneListing EnumerateHerdr(bool withPids)
    {
        const string failed = "herdr pane enumeration failed";
        var sessions = Herdr(null, ["session", "list"], ScreenCapBytes);
        if (Unanswered(sessions))
        {
            return new PaneListing([], Cause(failed, "herdr session list", sessions));
        }

        var sockets = new List<string>();
        if (sessions.Ok)
        {
            sockets.AddRange(SessionSockets(sessions.Stdout));
        }

        if (HerdrDefaultSocket is { Length: > 0 } fallback)
        {
            sockets.Add(fallback);
        }

        var instances = sockets.Distinct(StringComparer.Ordinal).Where(IsOwnSocket).Order(StringComparer.Ordinal).ToList();
        var truncated = instances.Count > MaxInstances;
        if (truncated)
        {
            instances = instances.Take(MaxInstances).ToList();
        }

        var found = new List<MuxPane>();
        var answered = instances.Count > 0;
        foreach (var socket in instances)
        {
            var panes = Herdr(socket, ["pane", "list"], ScreenCapBytes);
            if (Unanswered(panes))
            {
                return new PaneListing([], Cause(failed, $"herdr pane list (socket {socket})", panes));
            }

            if (!panes.Ok || !JsonText.TryParse(Utf8Text.Encode(panes.Stdout), out var doc) || Find(doc, "panes") is not { Kind: JsonKind.Array } list)
            {
                answered = false;
                continue;
            }

            var rows = list.AsArray();
            if (rows.Count > MaxPanesPerInstance)
            {
                truncated = true;
            }

            foreach (var row in rows.Take(MaxPanesPerInstance))
            {
                if (row.Member("pane_id") is not { IsText: true } id || !MuxIds.IsHerdrPaneId(id.AsString()))
                {
                    continue;
                }

                var tab = row.Member("tab_id") is { IsText: true } t && MuxIds.IsHerdrTabId(t.AsString()) ? t.AsString() : null;
                int? pid = null;
                if (withPids)
                {
                    var info = Herdr(socket, ["pane", "process-info", "--pane", id.AsString()], ScreenCapBytes);
                    if (Unanswered(info))
                    {
                        return new PaneListing([], Cause(failed, $"herdr pane process-info {id.AsString()} (socket {socket})", info));
                    }

                    // a pane whose process cannot be read stays listed, with no pid: it is there, and a caller must not take it for gone
                    if (info.Ok && JsonText.TryParse(Utf8Text.Encode(info.Stdout), out var detail) && ShellPid(detail) is { } shell)
                    {
                        pid = shell;
                    }
                }

                found.Add(new MuxPane("herdr", socket, id.AsString(), pid, tab));
            }
        }

        return new PaneListing(found, null, answered && !truncated, truncated);
    }

    private MuxResult Herdr(string? socket, string[] args, int outputCap) =>
        HerdrCalls.Run(args, CallTimeoutMs, outputCap, HerdrEnvironment(socket));

    /// <summary>The value of <paramref name="name"/> at the top of a herdr reply or under its <c>result</c>.</summary>
    private static JsonValue? Find(JsonValue doc, string name) =>
        doc.Member(name) is { } direct ? direct : doc.Member("result")?.Member(name);

    private static int? ShellPid(JsonValue doc)
    {
        if (Find(doc, "process_info")?.Member("shell_pid") is not { IsNumber: true } pid)
        {
            return null;
        }

        var n = pid.AsNumber();
        return n > 1 && n <= int.MaxValue && n == Math.Floor(n) ? (int)n : null;
    }

    /// <summary>
    /// The socket of each row of <c>herdr session list</c>: the trailing absolute-path field, taken from the last space that is
    /// followed by a <c>/</c>. A line without one (the header, a blank) names none.
    /// </summary>
    private static IEnumerable<string> SessionSockets(string table)
    {
        foreach (var line in table.Split('\n'))
        {
            var at = line.LastIndexOf(" /", StringComparison.Ordinal);
            if (at >= 0 && line[(at + 1)..].Trim() is { Length: > 1 } path)
            {
                yield return path;
            }
        }
    }

    /// <summary>The failure text with the command that did not answer, how it ended (signalled, or no exit status, with the runner's message) and its stderr.</summary>
    private static string Cause(string summary, string command, MuxResult result)
    {
        var how = result.Signalled ? "was signalled" : result.Status is { } code ? $"exited {code}" : "did not run to an exit";
        var detail = new[] { result.ErrorMessage, result.Stderr.Trim() }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Length > 200 ? t[..200] : t).ToList();
        return $"{summary}: `{command}` {how}{(detail.Count > 0 ? ": " + string.Join("; ", detail) : string.Empty)}";
    }

    /// <summary>A call that got no answer at all: it timed out, was cut off, or the binary is not there. A clean non-zero exit is an answer.</summary>
    private static bool Unanswered(MuxResult result) => result.Signalled || (!result.Ok && result.Status is null);

    /// <summary>Whether <paramref name="path"/> is one file directly inside the tmux socket directory, by name alone (nothing is read).</summary>
    private bool IsListedSocketPath(string path) =>
        path.StartsWith(TmuxDirectory + "/", StringComparison.Ordinal)
        && path[(TmuxDirectory.Length + 1)..] is { Length: > 0 } name
        && name != "." && name != ".."
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Whether <paramref name="path"/> is an absolute path with no control character or <c>..</c> segment, naming a socket owned by the user.</summary>
    private bool IsOwnSocket(string path) =>
        path.Length is > 1 and <= 1024 && path[0] == '/'
        && !path.Any(char.IsControl)
        && !path.Split('/').Contains("..")
        && Files.Stat(path, followSymlinks: false) is { Kind: FileKind.Other } stat && stat.Uid == Uid;

    private static string Failure(MuxResult result)
    {
        var text = result.Stderr + "\n" + result.Stdout;
        var gone = text.Contains("can't find pane", StringComparison.Ordinal)
            || text.Contains("pane_not_found", StringComparison.Ordinal)
            || text.Contains("pane not found", StringComparison.OrdinalIgnoreCase);
        return gone ? ClosedReason : UnreadableReason;
    }

    private static bool TryInt(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int CellCount(string line) => new StringInfo(Escapes().Replace(line, string.Empty)).LengthInTextElements;

    [GeneratedRegex("\u001b\\[[0-9;:?<=>]*[ -/]*[@-~]|\u001b\\][^\u0007\u001b]*(\u0007|\u001b\\\\)|\u001b[()][0-9A-Za-z]|\u001b.")]
    private static partial Regex Escapes();


}
