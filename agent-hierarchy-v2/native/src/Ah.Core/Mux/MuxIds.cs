using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Core.Mux;

/// <summary>
/// The accepted forms of the identifiers that reach a multiplexer's argv. Each form is the daemon's own enumeration output;
/// anything else is refused before an argv is built, because both multiplexers resolve looser spellings to some other target
/// (tmux treats <c>s1:0.0</c>, <c>@0</c> and <c>.</c> as targets, and a final <c>;</c> as a command separator).
/// </summary>
public static class MuxIds
{
    private const int MaxDigits = 9;
    private const int MaxLetters = 8;

    /// <summary>A tmux pane id: <c>%</c> and one to nine digits.</summary>
    public static bool IsTmuxPaneId(string? value)
    {
        if (value is null || value.Length < 2 || value.Length > 1 + MaxDigits || value[0] != '%')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A herdr pane id: <c>w&lt;digits&gt;&lt;letters&gt;:p&lt;digits&gt;&lt;letters&gt;</c>, one to nine digits and up to eight
    /// letters in each part. Case-sensitive and exact; a terminal id (<c>term_…</c>) is never a pane id. A local herdr core terminal
    /// address (<see cref="IsHerdrCoreAddress"/>) is accepted too.
    /// </summary>
    public static bool IsHerdrPaneId(string? value) => IsHerdrId(value, 'p') || IsHerdrCoreAddress(value);

    /// <summary>
    /// A herdr tab id: the pane id's shape with <c>t</c> for the second part, or a herdr desktop tab id, <c>tb</c> and 32 lowercase hex.
    /// </summary>
    public static bool IsHerdrTabId(string? value) => IsHerdrId(value, 't') || IsHerdrAppId(value, "tb");

    /// <summary>A local herdr core terminal address: <c>t</c>, a digit, then lowercase base-36 (<c>t7</c>, <c>t2x</c>); a tab id or a name is never one.</summary>
    public static bool IsHerdrCoreAddress(string? value) =>
        value is { Length: > 1 } && value[0] == 't' && IsDigit(value[1])
        && value.Skip(2).All(c => IsDigit(c) || c is >= 'a' and <= 'z');

    /// <summary>A herdr desktop id: <paramref name="prefix"/> and 32 lowercase hex (<c>tb…</c> for a tab, <c>sp…</c> for a space).</summary>
    public static bool IsHerdrAppId(string? value, string prefix) =>
        value is not null && value.Length == prefix.Length + 32 && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Skip(prefix.Length).All(c => IsDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>
    /// The directory tmux keeps its sockets in: <c>${TMUX_TMPDIR:-/tmp}/tmux-&lt;uid&gt;</c>. A null or empty
    /// <paramref name="tmuxTmpdir"/> means the variable is unset.
    /// </summary>
    public static string TmuxSocketDirectory(string? tmuxTmpdir, int uid)
    {
        var root = string.IsNullOrEmpty(tmuxTmpdir) ? "/tmp" : tmuxTmpdir.TrimEnd('/');
        return root + "/tmux-" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A tmux server socket the daemon listed: an absolute path whose real path is a direct child of the real path of
    /// <see cref="TmuxSocketDirectory"/> and equals the real path of one of <paramref name="listed"/>. Real paths are compared
    /// because tmux reports the resolved spelling (<c>/private/tmp/…</c> where <c>TMUX_TMPDIR</c> is unset on macOS, where the
    /// directory is <c>/tmp/tmux-&lt;uid&gt;</c>), and because a link must not redirect a socket out of the directory. The
    /// listing is the daemon's own, made from sockets the uid owns, so ownership is not re-read here.
    /// </summary>
    public static bool IsTmuxInstance(string? value, string? tmuxTmpdir, int uid, IReadOnlyCollection<string> listed, IFileReader files)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(files);
        if (!IsCleanAbsolutePath(value) || (!string.IsNullOrEmpty(tmuxTmpdir) && tmuxTmpdir[0] != '/'))
        {
            return false;
        }

        var realDirectory = RealPathResolver.ThroughLongestExistingPrefix(files, TmuxSocketDirectory(tmuxTmpdir, uid));
        var real = RealPathResolver.ThroughLongestExistingPrefix(files, value!);
        if (realDirectory is null || real is null)
        {
            return false;
        }

        var directory = realDirectory + "/";
        if (!real.StartsWith(directory, StringComparison.Ordinal))
        {
            return false;
        }

        var name = real[directory.Length..];
        if (name.Length == 0 || name == "." || name == ".." || name.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var entry in listed)
        {
            if (IsCleanAbsolutePath(entry)
                && string.Equals(RealPathResolver.ThroughLongestExistingPrefix(files, entry), real, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A herdr server socket: an absolute path that is one of <paramref name="listed"/> (the sockets of
    /// <c>herdr session list</c>, plus herdr's default socket when it is present).
    /// </summary>
    public static bool IsHerdrInstance(string? value, IReadOnlyCollection<string> listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return IsCleanAbsolutePath(value) && listed.Contains(value!);
    }

    private static bool IsHerdrId(string? value, char second)
    {
        if (value is null)
        {
            return false;
        }

        var i = 0;
        if (!ScanPart(value, ref i, 'w') || i >= value.Length || value[i] != ':')
        {
            return false;
        }

        i++;
        return ScanPart(value, ref i, second) && i == value.Length;
    }

    private static bool ScanPart(string value, ref int index, char prefix)
    {
        if (index >= value.Length || value[index] != prefix)
        {
            return false;
        }

        index++;
        var digits = 0;
        while (index < value.Length && IsDigit(value[index]))
        {
            digits++;
            index++;
        }

        if (digits < 1 || digits > MaxDigits)
        {
            return false;
        }

        var letters = 0;
        while (index < value.Length && IsLetter(value[index]))
        {
            letters++;
            index++;
        }

        return letters <= MaxLetters;
    }

    private static bool IsCleanAbsolutePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || value.Length > 1024)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
