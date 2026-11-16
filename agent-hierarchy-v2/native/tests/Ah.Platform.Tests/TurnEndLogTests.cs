using Ah.Abstractions;
using Ah.TestKit;

namespace Ah.Platform.Tests;

public sealed class TurnEndLogTests
{
    private static readonly DateTimeOffset Frozen = new(2026, 8, 16, 18, 32, 1, 123, TimeSpan.Zero);

    private static TurnEndLog LogAt(string path) => new(new FakeClock(Frozen), path, new RawFileWriter(), new RecordAppender());

    [Fact]
    public void ALineCarriesTheTurnEndAndWhatWasDoneWithIt()
    {
        var path = Path.Combine(Sandbox.NewDir(), "logs", "turn-ends.jsonl");

        LogAt(path).Record(new TurnEndRecord("StopFailure", "s1", null, "/d", "server_error", "s1:u-1", "recorded"));

        Assert.Equal(
            "{\"ts\":\"2026-08-16T18:32:01.123Z\",\"event\":\"StopFailure\",\"session\":\"s1\",\"agent_id\":null,\"dir\":\"/d\",\"error\":\"server_error\","
            + "\"turn_end_key\":\"s1:u-1\",\"action\":\"recorded\"}\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void AnActionIsCutAtThreeHundredCharacters()
    {
        var path = Path.Combine(Sandbox.NewDir(), "turn-ends.jsonl");

        LogAt(path).Record(new TurnEndRecord("detector", "s1", null, null, null, null, "error:" + new string('x', 900)));

        Assert.Contains("\"action\":\"error:" + new string('x', 294) + "\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTraceRotatesPastItsCap()
    {
        var path = Path.Combine(Sandbox.NewDir(), "turn-ends.jsonl");
        var old = new string('x', 1024 * 1024 + 1);
        File.WriteAllText(path, old);

        LogAt(path).Record(new TurnEndRecord("resume", "s1", null, null, null, null, "resumed"));

        Assert.Equal(old, File.ReadAllText(path + ".1"));
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void AnUnwritableLocationDoesNotThrow()
    {
        var blocker = Path.Combine(Sandbox.NewDir(), "file");
        File.WriteAllText(blocker, "not a directory");

        var ex = Record.Exception(() => LogAt(Path.Combine(blocker, "sub", "turn-ends.jsonl")).Record(new TurnEndRecord("resume", "s1", null, null, null, null, "resumed")));

        Assert.Null(ex);
    }
}
