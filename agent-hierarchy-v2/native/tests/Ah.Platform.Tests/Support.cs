using System.Reflection;

namespace Ah.Platform.Tests;

/// <summary>One scratch directory per test run, under the literal sandbox prefix. Nothing in it is ever deleted.</summary>
internal static class Sandbox
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var parent = Directory.Exists("/private/tmp") ? "/private/tmp" : "/tmp";
        var path = $"{parent}/ahprobe.{Guid.NewGuid().ToString("N")[..6]}";
        Directory.CreateDirectory(path);
        return path;
    });

    private static int _next;

    internal static string NewDir()
    {
        var path = Path.Combine(Root.Value, $"d{Interlocked.Increment(ref _next)}");
        Directory.CreateDirectory(path);
        return path;
    }
}

internal static class Repo
{
    internal static string NativeRoot { get; } = typeof(Repo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "AhNativeRoot").Value!;
}
