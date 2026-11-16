using System.Diagnostics;
using System.Text;
using Ah.Abstractions;

namespace Ah.Launch.Tests;

/// <summary>
/// A process runner over the real operating system for the tables that run Node's fake <c>herdr</c>, <c>ps</c> and <c>codex</c> scripts: stdin
/// closed, both pipes pumped at once, an output cap, and the whole process tree killed when the timeout passes. A binary that cannot be
/// started reports exit 127 with nothing on either stream.
/// </summary>
internal sealed class TestProcessRunner(IReadOnlyDictionary<string, string> baseEnvironment) : IProcessRunner
{
    private readonly object _gate = new();

    public List<ProcessRequest> Requests { get; } = [];

    public ProcessResult Run(ProcessRequest request)
    {
        lock (_gate)
        {
            Requests.Add(request);
        }

        var childEnvironment = new Dictionary<string, string>(baseEnvironment, StringComparer.Ordinal);
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null)
                {
                    childEnvironment.Remove(key);
                }
                else
                {
                    childEnvironment[key] = value;
                }
            }
        }

        // Process.Start resolves a bare name on this process's own PATH, never the child's: resolve on the child's, so a case can only
        // ever run the scripts it wrote and the system tools on the PATH it set.
        if (Resolve(request.Argv[0], childEnvironment.GetValueOrDefault("PATH")) is not { } executable)
        {
            return new ProcessResult(127, null, false, false, default, default);
        }

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Argv.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is not null)
        {
            start.WorkingDirectory = request.WorkingDirectory;
        }

        start.Environment.Clear();
        foreach (var (key, value) in childEnvironment)
        {
            start.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(127, null, false, false, default, default);
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // the child closed its stdin already
            }

            var cap = request.OutputCapBytes;
            var truncated = false;
            var stdout = Pump(process.StandardOutput.BaseStream, cap, () => truncated = true);
            var stderr = Pump(process.StandardError.BaseStream, cap, () => truncated = true);
            var timeout = request.Timeout ?? TimeSpan.FromSeconds(60);
            var exited = process.WaitForExit(timeout);
            var timedOut = false;
            if (!exited)
            {
                timedOut = true;
                KillTree(process);
                process.WaitForExit();
            }

            var outBytes = stdout.GetAwaiter().GetResult();
            var errBytes = stderr.GetAwaiter().GetResult();
            if (truncated && !timedOut)
            {
                KillTree(process);
            }

            return new ProcessResult(
                exited && !truncated ? process.ExitCode : null,
                null,
                timedOut,
                truncated,
                outBytes,
                errBytes);
        }
    }

    private static string? Resolve(string name, string? path)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(name) ? name : null;
        }

        foreach (var dir in (path ?? string.Empty).Split(':'))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<byte[]> Pump(Stream stream, int cap, Action overflow)
    {
        var buffer = new byte[8192];
        using var kept = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (kept.Length + read > cap)
            {
                overflow();
                kept.Write(buffer, 0, (int)Math.Max(0, cap - kept.Length));
                continue;
            }

            kept.Write(buffer, 0, read);
        }

        return kept.ToArray();
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }
}
