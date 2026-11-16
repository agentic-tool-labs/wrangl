using Ah.Core.Json;

namespace Ah.TestKit;

/// <summary>The decision rows without a usable id and the counts every reader of them must give, loaded from <c>tests/fixtures/decisions-idless</c>.</summary>
public static class IdlessDecisionsFixture
{
    private static readonly string Dir = Find();

    public static IReadOnlyList<string> Lines { get; } = [.. File.ReadAllLines(Path.Combine(Dir, "decisions.jsonl")).Where(static l => l.Length > 0)];

    public static JsonArray Rows()
    {
        var rows = new JsonArray();
        foreach (var line in Lines)
        {
            rows.Add(JsonText.Parse(line));
        }

        return rows;
    }

    public static JsonValue Expected() => JsonText.Parse(File.ReadAllText(Path.Combine(Dir, "expected.json")));

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "decisions-idless");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("tests/fixtures/decisions-idless not found above the test binaries");
    }
}
