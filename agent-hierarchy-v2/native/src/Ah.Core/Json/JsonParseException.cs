namespace Ah.Core.Json;

/// <summary>The input is not text that <c>JSON.parse</c> accepts, or a model is nested beyond <see cref="JsonText.MaxDepth"/>.</summary>
public sealed class JsonParseException : Exception
{
    public JsonParseException(string message)
        : base(message)
    {
    }

    public JsonParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JsonParseException()
    {
    }
}
