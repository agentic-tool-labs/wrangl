// Linked into every *.Tests project by Directory.Build.targets: each test process runs in a sandbox of its own and checks, when
// it ends, that it left the user's real ah2 state as it found it.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyFixture(typeof(Ah.TestProcess.RealHomeGuardFixture))]

namespace Ah.TestProcess;

/// <summary>
/// Makes the test process a test-mode process over a sandbox home before any test code runs. Product code that builds the
/// platform environment from the process (<c>PlatformModule.CreateIo(null)</c>, the hook composition, the hook-error log) then
/// resolves its home, its state home and its hook-error log inside the sandbox, as do the child processes that inherit the
/// environment. The real user's directories are never an input.
/// </summary>
internal static class ProcessSandbox
{
    private static string s_before = string.Empty;

    /// <summary>The password database's home for this user, read before the sandbox replaces <c>HOME</c>; null when it has none.</summary>
    internal static string? RealHome { get; private set; }

    /// <summary>The sandbox directory (<c>/private/tmp/ahprobe.*</c>), and what the process sees as its home inside it.</summary>
    internal static string Root { get; private set; } = string.Empty;

    internal static string Home => Path.Combine(Root, "home");

    [ModuleInitializer]
    internal static void Enter()
    {
        RealHome = PasswdHome();
        if (RealHome is { Length: > 0 })
        {
            s_before = RealHomeGuard.Snapshot(RealHome);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the native tests run on macOS and Linux");
        }

        var parent = Directory.Exists("/private/tmp") ? "/private/tmp" : "/tmp";
        Root = Path.Combine(parent, SandboxDirectory.Prefix +Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(Home, ".claude"));
        Directory.CreateDirectory(Path.Combine(Root, "tmp"));
        Environment.SetEnvironmentVariable("HOME", Home);
        Environment.SetEnvironmentVariable("TMPDIR", Path.Combine(Root, "tmp") + "/");
        Environment.SetEnvironmentVariable("AH_HOME", Path.Combine(Home, ".claude", "ah2"));
        Environment.SetEnvironmentVariable("AH_TEST", "1");
        if (RealHome is { Length: > 0 })
        {
            Environment.SetEnvironmentVariable("AH_TEST_REAL_HOME", RealHome);
        }

        RemoveAtExit(Root);
    }

    /// <summary>Removes <paramref name="root"/> (an <c>ahprobe.*</c> directory directly under /tmp or /private/tmp) when the process exits.</summary>
    internal static void RemoveAtExit(string root) => AppDomain.CurrentDomain.ProcessExit += (_, _) => SandboxDirectory.Remove(root);

    /// <summary>The paths under the real home that differ from how they were when the process started; null when none do.</summary>
    internal static string? RealHomeChanges()
    {
        if (RealHome is not { Length: > 0 })
        {
            return null;
        }

        return RealHomeGuard.Difference(s_before, RealHomeGuard.Snapshot(RealHome));
    }

    private static string? PasswdHome()
    {
        var entry = GetPwUid(GetUid());
        if (entry == 0)
        {
            return null;
        }

        // struct passwd: pw_dir follows pw_class and pw_change on macOS, and pw_gecos alone on Linux.
        var offset = OperatingSystem.IsMacOS() ? 48 : 32;
        var dir = Marshal.ReadIntPtr(entry, offset);
        return dir == 0 ? null : Marshal.PtrToStringUTF8(dir);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "getpwuid")]
    private static extern nint GetPwUid(uint uid);
}

/// <summary>
/// A fingerprint of what a home holds that ah2 would put there: every entry under <c>.claude/ah2</c>, each as kind, length and
/// modification time. Nothing of ah v1 is in it: a user who runs v1 has v1's files change at any time. Reads metadata only; nothing is
/// opened for writing and nothing is created.
/// </summary>
internal static class RealHomeGuard
{
    internal static string Snapshot(string home)
    {
        var lines = new List<string>();
        var ah2 = Path.Combine(home, ".claude", "ah2");
        Describe(lines, ah2);
        if (Directory.Exists(ah2))
        {
            Walk(lines, ah2, top: true);
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Lists every entry under a directory. A symbolic link is listed and never entered, so a link that points at its own parent ends
    /// the walk. The top level <c>tmp</c> is scratch space that members of a running team write to, not ah2 state.
    /// </summary>
    private static void Walk(List<string> lines, string directory, bool top)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            lines.Add(directory + "|unreadable");
            return;
        }

        foreach (var entry in entries)
        {
            if (top && Path.GetFileName(entry) == "tmp")
            {
                continue;
            }

            Describe(lines, entry);
            if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
            {
                Walk(lines, entry, top: false);
            }
        }
    }

    /// <summary>The entries that are in one fingerprint and not the other, one per line; null when the two are the same.</summary>
    internal static string? Difference(string before, string after)
    {
        if (before == after)
        {
            return null;
        }

        var was = before.Split('\n').ToHashSet(StringComparer.Ordinal);
        var now = after.Split('\n').ToHashSet(StringComparer.Ordinal);
        var changed = was.Except(now).Select(l => "was " + l).Concat(now.Except(was).Select(l => "now " + l)).Order(StringComparer.Ordinal).Take(20);
        return string.Join('\n', changed);
    }

    private static void Describe(List<string> lines, string path)
    {
        // One object per entry reads the entry once, so a live session removing it now cannot make Length throw after Exists said true.
        // This runs in the module initializer, where a throw aborts the test host (exit 134) before it finds a test.
        var directory = new DirectoryInfo(path);
        var file = new FileInfo(path);
        if (directory.Exists)
        {
            lines.Add($"{path}|dir|{directory.LastWriteTimeUtc.Ticks}");
        }
        else if (file.Exists)
        {
            lines.Add($"{path}|file|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        }
        else
        {
            lines.Add($"{path}|absent");
        }
    }
}

/// <summary>
/// Created with the test assembly and disposed after its last test. When the real user's ah2 state differs from how it was at
/// start it writes a note naming up to ten of the changes. It never fails the run: a live ah2 session on the same machine writes
/// there too, and a difference cannot be attributed to this process.
/// </summary>
public sealed class RealHomeGuardFixture : IDisposable
{
    private const int MaxListed = 10;

    private readonly Func<string?> _changes;
    private readonly Action<string> _note;

    public RealHomeGuardFixture() : this(ProcessSandbox.RealHomeChanges, Console.Error.WriteLine)
    {
    }

    internal RealHomeGuardFixture(Func<string?> changes, Action<string> note)
    {
        _changes = changes;
        _note = note;
    }

    public void Dispose()
    {
        if (_changes() is { } changed)
        {
            _note("ah: note: the real home's ah2 state changed during this run (another ah process?); the tripwire did not fire, so this run did not cause it:\n"
                + string.Join('\n', changed.Split('\n').Take(MaxListed)));
        }
    }
}
