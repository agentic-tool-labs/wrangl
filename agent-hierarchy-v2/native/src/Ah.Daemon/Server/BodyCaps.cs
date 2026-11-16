namespace Ah.Daemon;

/// <summary>The request body caps: 256 KiB for the three message writes, 64 KiB for every other route.</summary>
internal static class BodyCaps
{
    internal const int Default = 64 * 1024;
    internal const int MessageWrite = 256 * 1024;

    internal static int Of(string method, string path) =>
        method == "POST" && path is "/v1/state/msgs/create" or "/v1/state/msgs/settle" or "/v1/state/msgs/fill"
            ? MessageWrite
            : Default;
}
