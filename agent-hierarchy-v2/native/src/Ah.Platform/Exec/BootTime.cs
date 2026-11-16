using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Platform;

/// <summary>When the machine last booted: <c>sysctl kern.boottime</c> on macOS, <c>btime</c> in <c>/proc/stat</c> on Linux.</summary>
internal static class BootTime
{
    internal static DateTimeOffset? Read(IProcessRunner runner)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return File.Exists("/proc/stat") ? ParseProcStat(File.ReadAllText("/proc/stat")) : null;
            }

            var result = runner.Run(new ProcessRequest(["/usr/sbin/sysctl", "-n", "kern.boottime"], null, null, default, null, 4096));
            return result is { ExitCode: 0, TimedOut: false, Truncated: false } ? ParseSysctl(Utf8Text.Decode(result.Stdout.Span)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The boot instant in the output of <c>sysctl -n kern.boottime</c> (<c>{ sec = 1729785600, usec = 0 } …</c>), or null.</summary>
    internal static DateTimeOffset? ParseSysctl(string text)
    {
        var at = text.IndexOf("sec", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = text[(at + 3)..].TrimStart();
        if (!rest.StartsWith('='))
        {
            return null;
        }

        rest = rest[1..].TrimStart();
        var digits = rest.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && long.TryParse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The boot instant in the <c>btime</c> line of <c>/proc/stat</c>, or null.</summary>
    internal static DateTimeOffset? ParseProcStat(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["btime", var value] && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }
}
