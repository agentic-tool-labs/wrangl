using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>Checkpoint records (per-dir-files-to-db S11a: <c>checkpoints</c> rows keyed by the path the record file had), read and changed by that path the way a test once used the file.</summary>
public static class TestCheckpoints
{
    /// <summary>The record at <paramref name="path"/> as JSON text; throws as reading a missing file did.</summary>
    public static string Read(string ahHome, string path) =>
        StateDbModule.CreateCheckpoints(ahHome).ByKey(path)?.Body ?? throw new FileNotFoundException($"no checkpoint record {path}", path);

    /// <summary>Whether a record is stored at <paramref name="path"/>.</summary>
    public static bool Exists(string ahHome, string path) => StateDbModule.CreateCheckpoints(ahHome).ByKey(path) is not null;

    /// <summary>Replaces the body of the record at <paramref name="path"/>, as rewriting the file did.</summary>
    public static void Write(string ahHome, string path, string body)
    {
        var key = StateDbModule.CreateCheckpoints(ahHome).Key(path);
        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            tx.Execute("UPDATE checkpoints SET body = ?2 WHERE key = ?1", key, body);
            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException($"checkpoints: {written.Message}");
        }
    }
}
