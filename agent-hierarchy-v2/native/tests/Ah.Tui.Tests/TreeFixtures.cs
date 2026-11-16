using System.Text;
using Ah.Core.Json;

namespace Ah.Tui.Tests;

/// <summary>Reading fixtures and comparing goldens.</summary>
internal static class TreeFixtures
{
    private static readonly string GoldenDir = FindDir();

    internal static JsonValue Parse(string json) => JsonText.Parse(json);

    private static string FindDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Ah.Tui.Tests", "Goldens")))
        {
            dir = dir.Parent;
        }

        return Path.Combine((dir ?? throw new InvalidOperationException("tests/Ah.Tui.Tests/Goldens not found above the test binaries")).FullName, "tests", "Ah.Tui.Tests", "Goldens");
    }

    /// <summary>Compares <paramref name="actual"/> with the golden file, or rewrites it when <c>AH_UPDATE_GOLDENS</c> is set.</summary>
    internal static void Golden(string name, string actual)
    {
        var path = Path.Combine(GoldenDir, name);
        if (Environment.GetEnvironmentVariable("AH_UPDATE_GOLDENS") == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"golden {name} is missing; run with AH_UPDATE_GOLDENS=1");
        Assert.Equal(File.ReadAllText(path), actual);
    }
}
