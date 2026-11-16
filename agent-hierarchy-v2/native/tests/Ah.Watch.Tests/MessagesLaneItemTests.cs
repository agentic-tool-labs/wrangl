using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

public sealed class MessagesLaneItemTests
{
    private const string Dir = "/h/.ah";

    private sealed class Reader(string exchange) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => JsonText.Parse(exchange);
    }

    private static JsonValue RecordOf(string exchange) =>
        new MessagesLane(new Reader(exchange), () => [Dir]).Reread(MessagesLane.KeyOf(Dir, "20261031-120000-abcd"), 0).Record!.Value;

    [Fact]
    public void TheRecordCarriesTheExchangesItem()
    {
        var record = RecordOf("""{"team":"t","slug":"s","item":"S3","created":"2026-10-31T12:00:00Z","has_response":false}""");

        Assert.Equal("S3", record.Member("item")!.Value.AsString());
    }

    [Fact]
    public void AnExchangeWithNoItemHasANullItem()
    {
        var record = RecordOf("""{"team":"t","slug":"s","created":"2026-10-31T12:00:00Z","has_response":false}""");

        Assert.True(record.Member("item")!.Value.IsNull);
    }
}
