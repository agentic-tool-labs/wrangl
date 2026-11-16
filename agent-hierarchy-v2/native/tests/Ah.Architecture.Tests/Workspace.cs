using System.Reflection;

namespace Ah.Architecture.Tests;

internal static class Workspace
{
    internal static string NativeRoot { get; } = typeof(Workspace).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "AhNativeRoot").Value!;

    private static string OutputDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    internal static string BuiltAssembly(string project) => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(OutputDirectory))!,
        project,
        Path.GetFileName(OutputDirectory),
        Layers.AssemblyFile(project) + ".dll");

    internal static string SourceCsproj(string project) => Path.Combine(NativeRoot, "src", project, project + ".csproj");

    internal static string FixtureCsproj(string fixture) =>
        Path.Combine(NativeRoot, "tests", "fixtures", fixture, fixture + ".csproj");

    internal static string FixtureAssembly(string fixture) => BuiltAssembly(fixture);
}
