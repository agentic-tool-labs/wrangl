using System.Text;
using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>
/// The guard that keeps a test off the real terminal multiplexer and harness binaries. Outside test mode it does nothing. In test
/// mode with <c>AH_TEST_HERMETIC=1</c>, a guarded binary is only run when it resolves, on the PATH the call would use, to a file inside one of
/// the directories <c>AH_TEST_FAKE_BIN</c> lists; any other resolution is recorded in <c>AH_TEST_TRIPWIRE</c>, reported, the test
/// process (<c>AH_TEST_PID</c>) is terminated, and the call is refused. A binary found on no PATH entry is left to fail as it always did.
/// </summary>
public interface IMuxGuard
{
    /// <summary>The binaries a test must never reach for real: herdr, hc, herdr-app, tmux, claude and codex.</summary>
    IReadOnlyList<string> GuardedBinaries { get; }

    /// <summary>
    /// Checks one call of <paramref name="binary"/> with <paramref name="args"/>. <paramref name="pathVar"/> is the PATH the call would
    /// use; null means this process's own. Throws <see cref="InvalidOperationException"/> when the call would reach a real binary.
    /// </summary>
    void Guard(string binary, IReadOnlyList<string> args, string? pathVar = null);

    /// <summary>
    /// Checks the shell string a launch is about to run: each simple command whose command-position word is a guarded binary gets
    /// <see cref="Guard"/>'s check. A word in argument position is never checked, and <c>$(...)</c> and backticks are not parsed.
    /// </summary>
    void GuardShellCommand(string commandString, string? pathVar = null);
}

/// <summary>
/// The guard. <paramref name="OwnPid"/> is this process's pid, written on a tripwire row. <paramref name="ReportError"/> receives the
/// one-line message a tripwire reports (stderr, in a CLI). <paramref name="Terminate"/> sends SIGTERM to a pid; when it is null the
/// guard still records and refuses, and terminates nothing.
/// </summary>
public sealed record MuxGuard(
    IAhEnvironment Environment,
    IFileReader Files,
    IRecordAppender Appender,
    int OwnPid,
    Action<string>? ReportError = null,
    Func<int, bool>? Terminate = null) : IMuxGuard
{
    private static readonly string[] Binaries = ["herdr", "hc", "herdr-app", "tmux", "claude", "codex"];

    public IReadOnlyList<string> GuardedBinaries => Binaries;

    private bool Hermetic => Environment.GetTestOnly("AH_TEST_HERMETIC") == "1";

    public void Guard(string binary, IReadOnlyList<string> args, string? pathVar = null)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(args);
        if (!Hermetic)
        {
            return;
        }

        Check(binary, args, pathVar ?? Environment.GetVariable("PATH"));
    }

    public void GuardShellCommand(string commandString, string? pathVar = null)
    {
        ArgumentNullException.ThrowIfNull(commandString);
        if (!Hermetic)
        {
            return;
        }

        foreach (var words in ShellWords.SimpleCommands(commandString))
        {
            var at = ShellWords.CommandPosition(words);
            if (at < 0 || !Binaries.Contains(NodePath.Basename(words[at])))
            {
                continue;
            }

            Check(words[at], words.Skip(at + 1).ToArray(), pathVar ?? Environment.GetVariable("PATH"));
        }
    }

    private void Check(string binary, IReadOnlyList<string> args, string? pathVar)
    {
        if (!Binaries.Contains(NodePath.Basename(binary)))
        {
            return;
        }

        var found = ResolveOnPath(binary, pathVar);
        if (found is null)
        {
            return;
        }

        string real;
        try
        {
            real = Files.RealPath(found);
        }
        catch (Exception)
        {
            real = found;
        }

        foreach (var dir in ListedDirs())
        {
            if (real == dir || real.StartsWith(dir + "/", StringComparison.Ordinal))
            {
                return;
            }
        }

        Trip(binary, args);
    }

    private string? ResolveOnPath(string binary, string? pathVar)
    {
        if (binary.Contains('/', StringComparison.Ordinal))
        {
            return IsExecutableFile(binary) ? binary : null;
        }

        foreach (var dir in (pathVar ?? string.Empty).Split(':'))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            var candidate = NodePath.Join(dir, binary);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool IsExecutableFile(string path)
    {
        try
        {
            return Files.Stat(path) is { Kind: FileKind.File } && Files.IsExecutable(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private List<string> ListedDirs()
    {
        var dirs = new List<string>();
        foreach (var d in (Environment.GetTestOnly("AH_TEST_FAKE_BIN") ?? string.Empty).Split(':'))
        {
            if (d.Length == 0)
            {
                continue;
            }

            try
            {
                dirs.Add(Files.RealPath(d));
            }
            catch (Exception)
            {
                // a listed directory that does not exist lists nothing
            }
        }

        return dirs;
    }

    private void Trip(string binaryPath, IReadOnlyList<string> args)
    {
        var binary = NodePath.Basename(binaryPath);
        var line = $"{binary} {string.Join(' ', args)} pid={OwnPid}";
        try
        {
            if (Environment.GetTestOnly("AH_TEST_TRIPWIRE") is { Length: > 0 } tripwire)
            {
                Appender.Append(tripwire, Encoding.UTF8.GetBytes(line + "\n"));
            }
        }
        catch (Exception)
        {
            // the report and the termination below still happen
        }

        var message = $"ah: test hermeticity: a test reached the real {binary} ({string.Join(' ', args)}). Put a fake in a directory listed in AH_TEST_FAKE_BIN.";
        try
        {
            ReportError?.Invoke(message);
        }
        catch (Exception)
        {
            // a closed stderr
        }

        if (Environment.GetTestOnly("AH_TEST_PID") is { } text
            && Ah.Core.Json.JsConvert.ToNumber(Ah.Core.Json.JsonValue.FromString(text)) is var number
            && Math.Floor(number) == number
            && number is > 0 and <= int.MaxValue)
        {
            try
            {
                Terminate?.Invoke((int)number);
            }
            catch (Exception)
            {
                // the test file is already gone
            }
        }

        throw new InvalidOperationException(message);
    }
}

/// <summary>The shell-string splitter the guard reads a launch command with.</summary>
internal static class ShellWords
{
    internal static List<List<string>> SimpleCommands(string text)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        char? quote = null;

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
            }

            word.Clear();
            inWord = false;
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
            {
                commands.Add(words);
            }

            words = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }
                else if (c == '\\' && q == '"' && i + 1 < text.Length)
                {
                    word.Append(text[++i]);
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inWord = true;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                word.Append(text[++i]);
                inWord = true;
            }
            else if (c is ';' or '\n' or '|' or '&')
            {
                EndCommand();
                if (c is '|' or '&' && i + 1 < text.Length && text[i + 1] == c)
                {
                    i++;
                }
            }
            else if (JsText.IsSpace(c))
            {
                EndWord();
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        EndCommand();
        return commands;
    }

    /// <summary>Index of the command-position word: after leading <c>VAR=value</c> words and an optional exec, env or command; -1 when none.</summary>
    internal static int CommandPosition(IReadOnlyList<string> words)
    {
        var i = 0;
        while (i < words.Count)
        {
            var w = words[i];
            if (IsAssignment(w) || w is "exec" or "command")
            {
                i++;
            }
            else if (w == "env")
            {
                i++;
                while (i < words.Count && (IsAssignment(words[i]) || words[i].StartsWith('-')))
                {
                    i += words[i] == "-u" ? 2 : 1;
                }
            }
            else
            {
                break;
            }
        }

        return i < words.Count ? i : -1;
    }

    private static bool IsAssignment(string w)
    {
        if (w.Length == 0 || !(w[0] is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')))
        {
            return false;
        }

        var eq = w.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return false;
        }

        for (var k = 1; k < eq; k++)
        {
            if (!(w[k] is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }
}
