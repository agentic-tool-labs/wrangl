namespace Ah.Hierarchy;

/// <summary>An I/O failure that names its own error code, which an op reports as its <c>error_code</c> beside the message.</summary>
internal sealed class CodedIoException(string code, string message) : IOException(message)
{
    internal string Code { get; } = code;
}
