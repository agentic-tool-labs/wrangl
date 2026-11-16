using System.Text;
using System.Text.Json;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Core.Tests;

/// <summary>
/// The reader of ah2-1 (containers made at their exact size, short ASCII names shared, text parsed from a rented buffer) gives what the
/// reader it replaced gave: the same document, key order and strings as a plain walk that sets every member one at a time.
/// </summary>
public sealed class JsonReaderParityTests
{
    /// <summary>The old reader's shape: a new container at its start, each member set as it is read.</summary>
    private static JsonValue Reference(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject())
                {
                    o[p.Name] = Reference(p.Value);
                }

                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var item in e.EnumerateArray())
                {
                    a.Add(Reference(item));
                }

                return a;
            case JsonValueKind.String: return e.GetString()!;
            case JsonValueKind.Number: return e.GetDouble();
            case JsonValueKind.True: return JsonValue.True;
            case JsonValueKind.False: return JsonValue.False;
            default: return JsonValue.Null;
        }
    }

    private static string ManyNames()
    {
        // far more distinct short names than the name table has slots, each used twice, so slots are taken over and found again
        var sb = new StringBuilder("{");
        for (var i = 0; i < 6000; i++)
        {
            sb.Append(i == 0 ? string.Empty : ",").Append("\"k").Append(i % 3000).Append("\":\"v").Append(i % 7).Append('"');
        }

        return sb.Append('}').ToString();
    }

    public static TheoryData<string> Documents => new()
    {
        "{}", "[]", "[[],{},[{}]]", "\"text\"", "12.5", "null",
        "{\"a\":1,\"b\":{\"c\":[1,2,{\"d\":\"a\"}],\"a\":\"b\"},\"e\":[]}",
        "{\"a\":1,\"b\":2,\"a\":3}",                                   // a repeated key keeps its first place and its last value
        "{\"b\":1,\"2\":0,\"1\":0,\"01\":0,\"a\":{\"10\":1,\"9\":2}}",    // array-index keys come first, in numeric order
        "{\"é\":\"é\",\"k\":\"é\",\"a\\nb\":1,\"\":2}",                    // non-ASCII, escaped and empty names
        "{\"" + new string('n', 41) + "\":\"" + new string('v', 41) + "\",\"" + new string('n', 40) + "\":\"" + new string('n', 40) + "\"}",
        "{\"id\":\"id\",\"type\":\"request\",\"to\":\"to\",\"rows\":[{\"id\":\"1\",\"type\":\"request\"},{\"id\":\"2\",\"type\":\"response\"}]}",
        "[" + string.Join(",", Enumerable.Repeat("{\"id\":\"x\",\"n\":[1,[2,[3]]]}", 200)) + "]",
        string.Concat(Enumerable.Repeat("[{\"a\":", 100)) + "1" + string.Concat(Enumerable.Repeat("}]", 100)),
        ManyNames(),
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void AParsedDocumentIsWhatSettingEachMemberInTurnGives(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4096 });
        var expected = JsonText.Stringify(Reference(doc.RootElement), 0);

        // twice from bytes and once from text on one thread: the second and third meet the names the first left behind
        Assert.Equal(expected, JsonText.Stringify(JsonText.Parse(Encoding.UTF8.GetBytes(text)), 0));
        Assert.Equal(expected, JsonText.Stringify(JsonText.Parse(Encoding.UTF8.GetBytes(text)), 0));
        Assert.Equal(expected, JsonText.Stringify(JsonText.Parse(text), 0));
    }

    [Fact]
    public void NegativeAParseThatFailsHalfWayLeavesNothingForTheNextOne()
    {
        Assert.Throws<JsonParseException>(() => JsonText.Parse("{\"a\":[1,{\"b\":2,\"c\":[3,"));
        Assert.Throws<JsonParseException>(() => JsonText.Parse("{\"a\":1,}"));
        Assert.Equal("{\"x\":[1,{\"y\":2}]}", JsonText.Stringify(JsonText.Parse("{\"x\":[1,{\"y\":2}]}"), 0));
    }

    [Fact]
    public void TextIsParsedAsItsEncodedBytesAreIncludingALoneSurrogateAndInvalidUtf8()
    {
        foreach (var text in new[] { "{\"a\":\"x\ud83dy\"}", "{\"\ud83d\":1}", "[\"é😀\"]", "{\"k\":\"" + new string('z', 300_000) + "\"}" })
        {
            Assert.Equal(JsonText.Stringify(JsonText.Parse(Utf8Text.Encode(text)), 0), JsonText.Stringify(JsonText.Parse(text), 0));
        }

        // an invalid byte inside a short name decodes to U+FFFD, as before, and is not mistaken for a held ASCII name
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'a', 0xFF, (byte)'"', (byte)':', (byte)'1', (byte)'}' };
        var once = JsonText.Parse(bytes).AsObject();
        var again = JsonText.Parse(bytes).AsObject();
        Assert.Equal("a\ufffd", once.Keys[0]);
        Assert.Equal("a\ufffd", again.Keys[0]);
    }

    [Fact]
    public void AnObjectWithNoIndexKeyListsItsKeysInTheOrderSetAndTheListIsACopy()
    {
        var o = JsonText.Parse("{\"b\":1,\"a\":2,\"c\":3}").AsObject();
        var keys = o.Keys;
        Assert.Equal(["b", "a", "c"], keys);
        o["d"] = 4;
        _ = o.Remove("a");
        Assert.Equal(["b", "a", "c"], keys); // the list handed out earlier does not change under its reader
        Assert.Equal(["b", "c", "d"], o.Keys);
    }
}
