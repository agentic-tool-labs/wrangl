using Ah.Core.Json;

namespace Ah.StateDb.Tests;

/// <summary>files-off-disk §3.2: a take that throws any exception fails only its own group; the pass goes on and the dir stays incomplete.</summary>
public sealed class FilesImportFailureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ahfilesimport").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AGroupThrowingAnUnlistedExceptionIsRecordedFailedAndTheOtherGroupsImport()
    {
        var home = Path.Join(_root, "home");
        var hier = Path.Join(_root, "h");
        using (StateDbModule.Open(home, mayCreate: true))
        {
        }

        Directory.CreateDirectory(hier);
        File.WriteAllText(Path.Join(hier, "gates.jsonl"), "{\"gate\":1}\n");

        var record = JsonText.Parse(StateDbFilesImport.Import(home, hier, force: false, [("malformed", () => throw new FormatException("bad file"))])!);

        var groups = record.Member("groups")!.Value;
        Assert.Equal("bad file", groups.Member("malformed")!.Value.Member("failed")!.Value.AsString());
        Assert.Equal(1, groups.Member("gates")!.Value.Member("rows")!.Value.AsNumber());

        // negative: a dir with a failed group is not complete, so the next start runs it again
        Assert.False(StateDbFilesImport.Complete(home, hier));
    }
}
