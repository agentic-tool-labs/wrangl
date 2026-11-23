namespace Ah.Daemon;

/// <summary>
/// The request body caps: 4 MiB for the whole-team-record write, 256 KiB for the three message writes, 64 KiB for every other route.
/// </summary>
internal static class BodyCaps
{
    internal const int Default = 64 * 1024;
    internal const int MessageWrite = 256 * 1024;
    internal const int TeamRecordWrite = 4 * 1024 * 1024;

    internal static int Of(string method, string path) =>
        method != "POST" ? Default
        : path is "/v1/state/team/write" ? TeamRecordWrite
        : path is "/v1/state/msgs/create" or "/v1/state/msgs/settle" or "/v1/state/msgs/fill" ? MessageWrite
        : Default;
}
