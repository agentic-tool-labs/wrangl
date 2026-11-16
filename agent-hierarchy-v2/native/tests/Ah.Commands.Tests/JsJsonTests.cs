namespace Ah.Commands.Tests;

/// <summary>The message a JavaScript engine gives for text that is not JSON, for the inputs a flag value is likely to be.</summary>
public sealed class JsJsonTests
{
    [Theory]
    [InlineData("not-json", "Unexpected token 'o', \"not-json\" is not valid JSON")]
    [InlineData("", "Unexpected end of JSON input")]
    [InlineData("  ", "Unexpected end of JSON input")]
    [InlineData("{", "Expected property name or '}' in JSON at position 1 (line 1 column 2)")]
    [InlineData("{\"a\":", "Unexpected end of JSON input")]
    [InlineData("[1,2", "Expected ',' or ']' after array element in JSON at position 4 (line 1 column 5)")]
    [InlineData("{a:1}", "Expected property name or '}' in JSON at position 1 (line 1 column 2)")]
    [InlineData("[1,]", "Unexpected token ']', \"[1,]\" is not valid JSON")]
    [InlineData("{\"a\":1,}", "Expected double-quoted property name in JSON at position 7 (line 1 column 8)")]
    [InlineData("tru", "Unexpected end of JSON input")]
    [InlineData("\"abc", "Unterminated string in JSON at position 4 (line 1 column 5)")]
    [InlineData("[1 2]", "Expected ',' or ']' after array element in JSON at position 3 (line 1 column 4)")]
    [InlineData("{\"a\" 1}", "Expected ':' after property name in JSON at position 5 (line 1 column 6)")]
    [InlineData("1 2", "Unexpected non-whitespace character after JSON at position 2 (line 1 column 3)")]
    [InlineData("01", "Unexpected number in JSON at position 1 (line 1 column 2)")]
    [InlineData("-", "No number after minus sign in JSON at position 1 (line 1 column 2)")]
    [InlineData("1.", "Unterminated fractional number in JSON at position 2 (line 1 column 3)")]
    [InlineData("1e", "Exponent part is missing a number in JSON at position 2 (line 1 column 3)")]
    [InlineData("\"\\x\"", "Bad escaped character in JSON at position 2 (line 1 column 3)")]
    [InlineData("\"a\nb\"", "Bad control character in string literal in JSON at position 2 (line 1 column 3)")]
    [InlineData("[\"a\",not-json-at-all-with-more-than-twenty-chars]", "Unexpected token 'o', \"[\"a\",not-json-at\"... is not valid JSON")]
    [InlineData("{\"role\":\"x\",\"bad\"::1}", "Unexpected token ':', ...\"\"x\",\"bad\"::1}\" is not valid JSON")]
    [InlineData("[}", "Unexpected token '}', \"[}\" is not valid JSON")]
    [InlineData("x", "Unexpected token 'x', \"x\" is not valid JSON")]
    [InlineData("{\"a\":1}x", "Unexpected non-whitespace character after JSON at position 7 (line 1 column 8)")]
    [InlineData("[1,2]]", "Unexpected non-whitespace character after JSON at position 5 (line 1 column 6)")]
    [InlineData("{\"a\":1 \"b\":2}", "Expected ',' or '}' after property value in JSON at position 7 (line 1 column 8)")]
    [InlineData("[\"\\u12\"]", "Bad Unicode escape in JSON at position 6 (line 1 column 7)")]
    [InlineData("undefined", "\"undefined\" is not valid JSON")]
    [InlineData("[undefined]", "Unexpected token 'u', \"[undefined]\" is not valid JSON")]
    [InlineData("{\"a\":tru}", "Unexpected token '}', \"{\"a\":tru}\" is not valid JSON")]
    public void TextThatIsNotJsonGetsTheEnginesMessage(string text, string expected) =>
        Assert.Equal(expected, JsJson.Describe(text));

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("  1  ")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("{\"a\":[1,-2.5e3,true,false,null,\"x\\n\\u00e9\"],\"b\":{}}")]
    [InlineData("0")]
    [InlineData("-0.5")]
    public void JsonHasNoMessage(string text) => Assert.Null(JsJson.Describe(text));

    [Fact]
    public void AMessageOnALaterLineNamesItsLineAndColumn() =>
        Assert.Equal("Expected ',' or ']' after array element in JSON at position 6 (line 2 column 3)", JsJson.Describe("[1,\n2 3]"));

    [Fact]
    public void ParseFailsWithTheMessageAndReturnsTheValueOtherwise()
    {
        var failure = Assert.Throws<RosterFailure>(() => JsJson.Parse("not-json"));
        Assert.Equal("Unexpected token 'o', \"not-json\" is not valid JSON", failure.Message);
        Assert.True(JsJson.Parse("[1]").IsArray);
    }
}
