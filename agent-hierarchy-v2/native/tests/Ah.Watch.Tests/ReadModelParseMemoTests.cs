using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>b459 step 11: the manifest serves a held parsed body only while its slice keeps its version.</summary>
public sealed class ReadModelParseMemoTests
{
    private sealed class StatusLane : IModelLane
    {
        public string Word { get; set; } = "one";

        public string Name => "status";

        public IReadOnlyList<string> SliceIds(string key) => ["status:" + key];

        public IReadOnlyList<string> Keys() => [];

        public LaneResult Reread(string key, long nowMs) =>
            new([new SliceChange("status:" + key, JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["word"] = Word }), 0), 0)]);
    }

    private static string Served(ReadModel model) =>
        JsonText.Parse(model.Manifest("status")!).Member("slices")!.Value.AsArray().Single().Member("body")!.Value.AsObject().Str("word")!;

    private static void Load(ReadModel model, ref long now)
    {
        model.Emit("status", "k");
        for (var guard = 0; guard < 10_000; guard++)
        {
            if (model.Projector.Step())
            {
                continue;
            }

            if (model.Bus.NextDueInMs() is { } due && due > 0)
            {
                now += due;
                continue;
            }

            return;
        }
    }

    [Fact]
    public void ASliceRepublishedAtANewVersionIsServedWithItsNewBodyNotTheHeldOne()
    {
        var now = 1000L;
        var lane = new StatusLane();
        using var model = new ReadModel([lane], new ModelOptions { SeedAtStart = false }, () => now, null, "e", null, new ComposerRegistry());
        Load(model, ref now);
        Assert.Equal("one", Served(model));
        Assert.Equal("one", Served(model));

        lane.Word = "two";
        Load(model, ref now);
        Assert.Equal("two", Served(model));
    }
}
