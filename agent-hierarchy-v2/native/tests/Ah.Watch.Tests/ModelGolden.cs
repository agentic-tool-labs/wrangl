using System.Runtime.CompilerServices;
using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>Compares a document with the committed JSON file of the same name; <c>AH_UPDATE_GOLDENS=1</c> rewrites the file.</summary>
internal static class ModelGolden
{
    internal static void Check(JsonObject document, string name, [CallerFilePath] string caller = "") => Check(JsonValue.FromObject(document), name, caller);

    internal static void Check(JsonValue document, string name, [CallerFilePath] string caller = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(caller)!, "Goldens", name + ".json");
        var text = JsonText.Stringify(document, 2) + "\n";
        if (Environment.GetEnvironmentVariable("AH_UPDATE_GOLDENS") == "1")
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return;
        }

        Assert.True(File.Exists(path), $"no golden {path}; run with AH_UPDATE_GOLDENS=1");
        Assert.Equal(File.ReadAllText(path), text);
    }
}
