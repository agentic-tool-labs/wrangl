using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>b459 step 28: the messages lane's keys are held per dir on the message store version.</summary>
public sealed class LaneKeyHoldTests
{
    private sealed class KeysReader : IViewStateReader
    {
        internal long Version { get; set; } = 1;

        internal List<string> Ids { get; } = ["20260101-000001-a001"];

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => (domain, op) switch
        {
            ("msgs", "version") => new JsonObject { ["version"] = Version },
            ("msgs", "keys") => new JsonObject { ["ids"] = JsonValue.FromArray([.. Ids.Select(static i => JsonValue.FromString(i))]) },
            _ => JsonValue.Null,
        };
    }

    [Fact]
    public void AnUnchangedVersionReusesTheKeysAndNegativeAMovedVersionListsThemAgain()
    {
        var reader = new KeysReader();
        var lane = new MessagesLane(reader, static () => ["/d", "/e"]);

        var first = lane.Keys();
        Assert.Equal(2, lane.KeyReads);
        Assert.Equal(first, lane.Keys());
        Assert.Equal(2, lane.KeyReads);

        reader.Ids.Add("20260101-000002-a002");
        reader.Version = 2;
        var moved = lane.Keys();
        Assert.Equal(4, lane.KeyReads);
        Assert.Equal(4, moved.Count);
    }
}
