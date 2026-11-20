using Ah.Core.Json;
using Ah.Tui.Core;

namespace Ah.Tui.Tests;

/// <summary>The TUI's status language is tests/fixtures/status-language.json, so the two cannot drift.</summary>
public sealed class StatusLanguageTests
{
    private static string FixtureText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "status-language.json")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine((dir ?? throw new InvalidOperationException("tests/fixtures/status-language.json not found above the test binaries")).FullName, "tests", "fixtures", "status-language.json"));
    }

    private static JsonValue M(JsonValue v, string key) => v.Member(key) ?? throw new InvalidOperationException("fixture has no " + key);

    /// <summary>Every way the TUI's table and <paramref name="fixture"/> differ; empty when they are the same.</summary>
    private static List<string> Differences(JsonValue fixture)
    {
        var diffs = new List<string>();
        var states = M(fixture, "states");
        var keys = states.AsObject().Keys;
        var mine = StatusLanguage.All.Select(s => s.Key).ToList();
        if (!mine.Order(StringComparer.Ordinal).SequenceEqual(keys.Order(StringComparer.Ordinal)))
        {
            diffs.Add($"states: tui [{string.Join(",", mine)}] fixture [{string.Join(",", keys)}]");
        }

        foreach (var s in StatusLanguage.All.Where(s => keys.Contains(s.Key)))
        {
            var f = M(states, s.Key);
            var want = (M(f, "glyph").AsString(), M(f, "word").AsString(), M(f, "tone").AsString(), (int)M(f, "rank").AsNumber());
            if ((s.Glyph, s.Word, s.Tone, s.Rank) != want)
            {
                diffs.Add($"{s.Key}: tui {(s.Glyph, s.Word, s.Tone, s.Rank)} fixture {want}");
            }
        }

        Compare("from", StatusLanguage.From, M(fixture, "from"), diffs);
        Compare("liveness", StatusLanguage.Liveness, M(fixture, "liveness"), diffs);
        return diffs;
    }

    private static void Compare(string name, IReadOnlyDictionary<string, string> mine, JsonValue theirs, List<string> diffs)
    {
        var map = theirs.AsObject().Keys.ToDictionary(k => k, k => M(theirs, k).AsString(), StringComparer.Ordinal);
        if (mine.Count != map.Count || mine.Any(kv => !map.TryGetValue(kv.Key, out var v) || v != kv.Value))
        {
            diffs.Add($"{name}: tui {{{string.Join(",", mine.Select(kv => kv.Key + "=" + kv.Value))}}} fixture {{{string.Join(",", map.Select(kv => kv.Key + "=" + kv.Value))}}}");
        }
    }

    [Fact]
    public void TheTuiTableIsTheFixture() => Assert.Empty(Differences(JsonText.Parse(FixtureText())));

    [Fact]
    public void DoneTakesTheOkTone() => Assert.Equal("ok", StatusLanguage.Get("done").Tone);

    [Fact]
    public void NegativeAFixtureWithDoneIdleDiffers()
    {
        var text = FixtureText();
        var changed = text.Replace("\"word\": \"done\", \"tone\": \"ok\"", "\"word\": \"done\", \"tone\": \"idle\"", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        Assert.Contains(Differences(JsonText.Parse(changed)), d => d.StartsWith("done:", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAFixtureWithoutHeldDiffers()
    {
        var text = FixtureText();
        var changed = text.Replace("\"held\":", "\"heldX\":", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        Assert.Contains(Differences(JsonText.Parse(changed)), d => d.StartsWith("states:", StringComparison.Ordinal));
    }
}
