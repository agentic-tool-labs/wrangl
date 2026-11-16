using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>Replaces the process image with <c>execve</c>, after putting the signals in <see cref="Libc.ChildDefaultSignals"/> at their default action; a failed <c>execve</c> restores them.</summary>
/// <param name="signals">The signals to reset; null is <see cref="Libc.ChildDefaultSignals"/>. A test names others, since resetting SIGUSR1 in a live test host can kill it.</param>
internal sealed class PosixExec(IReadOnlyList<int>? signals = null) : IExec
{
    [DoesNotReturn]
    public void Exec(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (argv.Count == 0)
        {
            throw new ArgumentException("argv is empty", nameof(argv));
        }

        var errno = TryExec(argv, BuildEnvironment(environment));
        throw new IOException($"execve {argv[0]} failed (errno {errno})");
    }

    /// <summary>Returns only when <c>execve</c> failed, with the errno.</summary>
    private unsafe int TryExec(IReadOnlyList<string> argv, IReadOnlyList<string> envp)
    {
        var allocations = new List<nint>();
        try
        {
            var path = Alloc(argv[0], allocations);
            var argvPtr = AllocArray(argv, allocations);
            var envPtr = AllocArray(envp, allocations);

            return ExecWithDefaultSignals(
                signals ?? Libc.ChildDefaultSignals,
                () =>
                {
                    Libc.Execve((byte*)path, (byte**)argvPtr, (byte**)envPtr);
                    return Marshal.GetLastPInvokeError();
                });
        }
        finally
        {
            foreach (var pointer in allocations)
            {
                NativeMemory.Free((void*)pointer);
            }
        }
    }

    /// <summary>
    /// Sets each of <paramref name="signals"/> to its default action, then calls <paramref name="execve"/>. It returns only when
    /// <paramref name="execve"/> did, with its errno, or when a signal could not be reset, with that errno and
    /// <paramref name="execve"/> not called. Either way every action it changed is put back exactly as it was found, flags and
    /// mask included, so a failed exec leaves this process handling its signals as before.
    /// </summary>
    internal static unsafe int ExecWithDefaultSignals(IReadOnlyList<int> signals, Func<int> execve)
    {
        var size = Libc.SigactionBytes;
        var saved = (byte*)NativeMemory.AllocZeroed((nuint)((signals.Count + 1) * size));
        try
        {
            var defaultAction = saved + (signals.Count * size);
            var changed = 0;
            var errno = 0;
            while (changed < signals.Count && errno == 0)
            {
                if (Libc.Sigaction(signals[changed], defaultAction, saved + (changed * size)) == 0)
                {
                    changed++;
                }
                else
                {
                    errno = Marshal.GetLastPInvokeError();
                }
            }

            if (errno == 0)
            {
                errno = execve();
            }

            for (var i = 0; i < changed; i++)
            {
                Libc.Sigaction(signals[i], saved + (i * size), null);
            }

            return errno;
        }
        finally
        {
            NativeMemory.Free(saved);
        }
    }

    private static List<string> BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            merged[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                if (value is null)
                {
                    merged.Remove(name);
                }
                else
                {
                    merged[name] = value;
                }
            }
        }

        return [.. merged.Select(p => p.Key + "=" + p.Value)];
    }

    private static unsafe nint Alloc(string value, List<nint> allocations)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        allocations.Add((nint)memory);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        memory[bytes.Length] = 0;
        return (nint)memory;
    }

    private static unsafe nint AllocArray(IReadOnlyList<string> values, List<nint> allocations)
    {
        var array = (nint*)NativeMemory.Alloc((nuint)(values.Count + 1) * (nuint)sizeof(nint));
        allocations.Add((nint)array);
        for (var i = 0; i < values.Count; i++)
        {
            array[i] = Alloc(values[i], allocations);
        }

        array[values.Count] = 0;
        return (nint)array;
    }
}
