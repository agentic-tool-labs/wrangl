using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>Reading an op's arguments and shaping its result as the engine expects.</summary>
internal static class OpArgs
{
    /// <summary>The call's args. JSON <c>null</c> (or text that is not JSON) fails the op, as reading a property of it does in Node.</summary>
    internal static JsonValue Parse(OpCall call)
    {
        var value = JsonText.Parse(call.ArgsJson.Span);
        if (value.IsNull)
        {
            throw new InvalidOperationException("Cannot read properties of null");
        }

        return value;
    }

    /// <summary>Runs an op body: a result becomes the op's JSON, any failure becomes the op's error message (and its own code, when it names one).</summary>
    // handOut: true only for a body whose result is built for this call and referenced nowhere else; the result then also carries the value itself (OpResult.Value)
    internal static OpResult Run(OpCall call, Func<JsonValue, JsonValue> body, bool handOut = false)
    {
        try
        {
            var value = body(Parse(call));
            return new OpResult(true, JsonText.ToUtf8(value, 0), Value: handOut ? (object)value : null);
        }
        catch (CodedIoException e)
        {
            return new OpResult(false, default, e.Message, e.Code);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OpResult(false, default, e.Message);
        }
    }

    /// <summary>The text of an args member when it is a string, else null.</summary>
    internal static string? Text(JsonValue args, string name) => args.Member(name) is { IsText: true } t ? t.AsString() : null;
}
