using System.Security.Cryptography;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Platform.Interop;

namespace Ah.Platform;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>The offset of <see cref="TimeZoneInfo.Local"/>, which follows <c>TZ</c> on Unix.</summary>
    public TimeSpan LocalOffset(DateTimeOffset instant) => TimeZoneInfo.Local.GetUtcOffset(instant);
}

internal sealed class SystemRandom : IRandom
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

/// <summary>
/// Environment variables as <c>process.env</c> gives them, the home as the passwd entry gives it, and the one test-mode predicate.
/// Outside test mode <c>HOME</c> is never read; in test mode it is honoured.
/// </summary>
internal sealed class SystemEnvironment : IAhEnvironment, IPinnedAhHome
{
    private const string DefaultHomeUnderUser = Ah.Core.Text.AhPaths.HomeRelative;

    private readonly Func<string, string?> _getVariable;
    private readonly Func<string?> _passwdHome;
    private readonly IFileReader _files;
    private readonly uint _uid;
    private readonly Lazy<bool> _testMode;
    private readonly Lazy<(string Path, string? Resolved)> _ahHome;

    internal SystemEnvironment(PluginInfo? plugin)
        : this(Environment.GetEnvironmentVariable, plugin, new FileReader(), Libc.GetUid(), PlatformModule.PasswdHome)
    {
    }

    internal SystemEnvironment(Func<string, string?> getVariable, PluginInfo? plugin, IFileReader files, uint uid, Func<string?> passwdHome)
    {
        _getVariable = getVariable;
        _passwdHome = passwdHome;
        _files = files;
        _uid = uid;
        Plugin = plugin;
        _testMode = new Lazy<bool>(() => TestModePredicate.Holds(getVariable("AH_TEST"), getVariable("AH_HOME"), files, uid));
        _ahHome = new Lazy<(string Path, string? Resolved)>(ResolveAhHome);
    }

    public PluginInfo? Plugin { get; }

    /// <summary>Whether <see cref="IAhEnvironment.TestMode"/> holds; computed once per process.</summary>
    public bool TestMode => _testMode.Value;

    public bool TestModeHolds(string? ahTest, string? ahHome) => TestModePredicate.Holds(ahTest, ahHome, _files, _uid);

    /// <summary>The environment variable a test process sets to the real home its <c>HOME</c> replaced.</summary>
    internal const string RealHomeMarker = "AH_TEST_REAL_HOME";

    /// <summary>
    /// The passwd entry's home; in test mode <c>HOME</c> when it is set (even empty). When <c>AH_TEST_REAL_HOME</c> is set, a home
    /// that is that directory or inside it (compared by real path) is never returned: the caller is a test process, or a child of
    /// one, about to reach the real user's state.
    /// </summary>
    public string HomeDirectory
    {
        get
        {
            var home = TestMode && _getVariable("HOME") is { } set
                ? set
                : _passwdHome() is { Length: > 0 } passwd ? passwd : throw new InvalidOperationException("the passwd database has no home directory for this user; HOME is not used outside test mode");
            RefuseRealHome(home, "HOME");
            return home;
        }
    }

    /// <summary>
    /// Throws when <c>AH_TEST_REAL_HOME</c> is set and <paramref name="path"/> is that directory or anywhere inside it. Both are
    /// compared by real path, the nearest existing ancestor resolved when the path does not exist yet. <paramref name="door"/>
    /// names where the path came from.
    /// </summary>
    private void RefuseRealHome(string path, string door)
    {
        if (_getVariable(RealHomeMarker) is not { Length: > 0 } real || path.Length == 0)
        {
            return;
        }

        var resolvedPath = Resolved(path);
        var resolvedReal = Resolved(real);
        if (resolvedPath == resolvedReal || resolvedPath.StartsWith(resolvedReal + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"ah: a test process resolved the real home ({path}) through {door}");
        }
    }

    private string Resolved(string path) => (TestModePredicate.ResolveThroughLongestExistingPrefix(_files, path) ?? path).TrimEnd('/');

    /// <summary>Whether this is test mode with <c>HOME</c> (unset reads as the passwd home) being the passwd entry's home, compared by real path.</summary>
    public bool TestModeInRealHome =>
        TestMode && _passwdHome() is { Length: > 0 } passwd
        && (_getVariable("HOME") ?? passwd) is { Length: > 0 } home
        && Resolved(home) == Resolved(passwd);

    /// <summary>
    /// <c>AH_HOME</c> trimmed when non-empty, else <c>~/.claude/ah2</c>. Outside test mode an absolute path is its
    /// resolved real path, worked out once per process: the home the gate checked is the home every later use gets, even if a
    /// link in it is retargeted in between.
    /// </summary>
    public string AhHome => _ahHome.Value.Resolved ?? _ahHome.Value.Path;

    /// <summary>The resolved real path <see cref="AhHome"/> returns; null in test mode, for a relative path or one that cannot be resolved.</summary>
    public string? ResolvedAhHome => _ahHome.Value.Resolved;

    private (string Path, string? Resolved) ResolveAhHome()
    {
        var configured = _getVariable("AH_HOME");
        var fromVariable = configured is not null && JsText.Trim(configured) is { Length: > 0 };
        var path = fromVariable ? JsText.Trim(configured!) : JoinUnderHome(HomeDirectory);
        RefuseRealHome(path, fromVariable ? "AH_HOME" : "HOME");
        return TestMode || path.Length == 0 || path[0] != '/'
            ? (path, null)
            : (path, TestModePredicate.ResolveThroughLongestExistingPrefix(_files, path));
    }

    public string? GetVariable(string name) => _getVariable(name);

    public string? GetTestOnly(string name) => TestMode ? _getVariable(name) : null;

    public bool TestLimiterOff => _getVariable("AH_TEST_SLOTS") == "0" || _getVariable("AH_TEST_TOKENS") == "0";

    private static string JoinUnderHome(string home) =>
        home.Length == 0 ? DefaultHomeUnderUser : home.TrimEnd('/') + "/" + DefaultHomeUnderUser;
}

internal sealed class ProcessInspector : IProcessInspector
{
    /// <summary><c>process.kill(pid, 0)</c>: alive when it succeeds and when it fails with EPERM.</summary>
    public bool PidAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        return Libc.Kill(pid, 0) == 0 || Libc.Errno == Libc.Eperm;
    }

    public bool PidOwned(int pid) =>
        pid > 0 && (Ah.Core.Time.ProcessSnapshotScope.ActivePass is { } pass ? pass.PerPid("owned", pid, static p => Libc.Kill(p, 0) == 0) : Libc.Kill(pid, 0) == 0);

    public string? CommandLine(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            return OperatingSystem.IsMacOS() ? DarwinCommandLine(pid) : File.Exists($"/proc/{pid}/cmdline") ? LinuxCommandLine(File.ReadAllBytes($"/proc/{pid}/cmdline")) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><c>/proc/&lt;pid&gt;/cmdline</c>: the arguments, each ended by a NUL; joined by spaces. Null for a process with none (a kernel thread or a zombie).</summary>
    internal static string? LinuxCommandLine(ReadOnlySpan<byte> raw)
    {
        var text = System.Text.Encoding.UTF8.GetString(raw).TrimEnd('\0');
        return text.Length == 0 ? null : text.Replace('\0', ' ');
    }

    private static string? DarwinCommandLine(int pid)
    {
        const int CtlKern = 1;
        const int KernProcArgs2 = 49;
        Span<int> name = [CtlKern, KernProcArgs2, pid];
        nuint length = 0;
        if (Libc.Sysctl(name, 3, [], ref length, 0, 0) != 0 || length == 0)
        {
            return null;
        }

        // asked for every member on every liveness pass: the block is parsed into a string before the array goes back
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            nuint filled = (nuint)buffer.Length;
            if (Libc.Sysctl(name, 3, buffer, ref filled, 0, 0) != 0)
            {
                return null;
            }

            return DarwinCommandLine(buffer.AsSpan(0, (int)filled));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// A <c>KERN_PROCARGS2</c> buffer: an <c>int</c> argument count, the executable path, NUL padding, then <c>argc</c> NUL-ended
    /// arguments (the environment follows and is not read). Null when the buffer is shorter than its own layout.
    /// </summary>
    internal static string? DarwinCommandLine(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < sizeof(int))
        {
            return null;
        }

        var argc = BitConverter.ToInt32(raw[..sizeof(int)]);
        var at = sizeof(int);
        while (at < raw.Length && raw[at] != 0)
        {
            at++;
        }

        while (at < raw.Length && raw[at] == 0)
        {
            at++;
        }

        var args = new List<string>();
        while (args.Count < argc && at < raw.Length)
        {
            var end = at;
            while (end < raw.Length && raw[end] != 0)
            {
                end++;
            }

            args.Add(System.Text.Encoding.UTF8.GetString(raw[at..end]));
            at = end + 1;
        }

        return args.Count == 0 ? null : string.Join(' ', args);
    }
}
