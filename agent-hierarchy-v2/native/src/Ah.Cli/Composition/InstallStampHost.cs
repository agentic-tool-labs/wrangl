using System.Text;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Hooks;

namespace Ah.Cli.Composition;

/// <summary>The install-stamp seam over the real file writer and the detached spawner.</summary>
internal sealed class InstallStampHost(IRawFileWriter writer, IDetachedSpawner spawner) : IInstallStampHost
{
    public void WriteStamp(string path, string text)
    {
        writer.Mkdir(NodePath.Dirname(path));
        writer.WriteAtomic(path, $"{path}.{Environment.ProcessId}.tmp", Encoding.UTF8.GetBytes(text));
    }

    public void StartRebuild(string script, string home, IReadOnlyDictionary<string, string?> environment)
    {
        var logs = NodePath.Join(home, "logs");
        writer.Mkdir(logs);
        spawner.SpawnDetached(
            new DetachedSpawnRequest(["/bin/sh", script, "--apply", "--rebuild-only", "--quiet"], home, environment, NodePath.Join(logs, "rebuild.log")));
    }
}
