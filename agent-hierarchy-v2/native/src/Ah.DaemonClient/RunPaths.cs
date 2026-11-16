namespace Ah.DaemonClient;

/// <summary>The files of one state home that the daemon's lifecycle uses.</summary>
internal sealed class RunPaths(string ahHome)
{
    /// <summary>Longest socket path, in bytes, either runtime binds.</summary>
    internal const int MaxSocketPath = 103;

    internal string Home { get; } = ahHome;

    internal string Run { get; } = Path.Combine(ahHome, "run");

    internal string Sock { get; } = Path.Combine(ahHome, "run", "ahd.sock");

    internal string Lock { get; } = Path.Combine(ahHome, "run", "ahd.lock");

    /// <summary>Held (flock) by a daemon for its whole life, dropped by the kernel when it exits: held means a live ahd, record or no record.</summary>
    internal string Life { get; } = Path.Combine(ahHome, "run", "ahd.life.lock");

    internal string Info { get; } = Path.Combine(ahHome, "run", "ahd.json");

    internal string PidFile { get; } = Path.Combine(ahHome, "run", "ahd.pid");

    internal string SpawnFailed { get; } = Path.Combine(ahHome, "run", "ahd.spawn-failed");

    internal string Log { get; } = Path.Combine(ahHome, "logs", "ahd.log");

    internal string Fallback { get; } = Path.Combine(ahHome, "logs", "fallback.jsonl");

    internal string Rejected { get; } = Ah.Core.Text.AhPaths.RejectedLog(ahHome);

    internal static int SocketBytes(string path) => System.Text.Encoding.UTF8.GetByteCount(path);
}
