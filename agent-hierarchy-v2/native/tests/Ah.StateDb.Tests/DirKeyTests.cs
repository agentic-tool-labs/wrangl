using Ah.Core.Json;
using Ah.Watch;

namespace Ah.StateDb.Tests;

/// <summary>b442 (adr-21): every dir-keyed table keys one directory one way, so a row written through a symlink is read through its target.</summary>
public sealed class DirKeyTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ahdirkey").FullName;
    private readonly string _base = Directory.CreateTempSubdirectory("ahdirkeydirs").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public void ARowWrittenThroughASymlinkIsReadThroughItsTarget()
    {
        var real = Directory.CreateDirectory(Path.Join(_base, "real")).FullName;
        var link = Path.Join(_base, "link");
        _ = Directory.CreateSymbolicLink(link, real);
        File.WriteAllText(Path.Join(real, "gates.jsonl"), "{\"g\":1}\n");
        File.WriteAllText(Path.Join(real, "qa-log.jsonl"), "{\"id\":\"q1\",\"at\":1}\n");
        Directory.CreateDirectory(Path.Join(real, "pipeline", "run1"));
        File.WriteAllText(Path.Join(real, "pipeline", "run1", "decisions.jsonl"), "{\"d\":1}\n");
        using (StateDbModule.Open(_home, mayCreate: true))
        {
        }

        Assert.True(new StateDbGates(_home).Take(link));
        Assert.True(new StateDbQaLog(_home).Take(link));
        Assert.True(new StateDbDecisions(_home).TakeRuns(link));

        Assert.Equal(["{\"g\":1}"], StateDbModule.CreateGates(_home).Bodies(real + "/"));
        Assert.Equal(["{\"id\":\"q1\",\"at\":1}"], StateDbModule.CreateQaLog(_home).Bodies(real + "/"));
        Assert.Equal(["{\"d\":1}"], StateDbModule.CreateDecisions(_home).Bodies(real + "/", "run1"));

        // the negative: another dir beside the target reads none of its rows
        var other = Directory.CreateDirectory(Path.Join(_base, "other")).FullName;
        Assert.Empty(StateDbModule.CreateGates(_home).Bodies(other));

        Assert.Equal(WatchModule.RecoverDirKey(real), WatchModule.RecoverDirKey(link));
        Assert.NotEqual(WatchModule.RecoverDirKey(real), WatchModule.RecoverDirKey(other));
    }

    [Fact]
    public void TheImportListKeepsTheFirstSpellingOfADirAndDropsItsOtherSpellings()
    {
        var real = Directory.CreateDirectory(Path.Join(_base, "imp")).FullName;
        var link = Path.Join(_base, "implink");
        _ = Directory.CreateSymbolicLink(link, real);
        var other = Directory.CreateDirectory(Path.Join(_base, "impother")).FullName;

        // b442 Amendment 2: deduped on the key, shown as listed; the negative: a different dir is kept
        Assert.Equal([link, other], StateDbFilesImport.Dirs(_home, [link, real + "/", other]));
    }

    [Fact]
    public void PoolKeyLeavesEmptyAndRelativeAsGivenButResolvesAnAbsoluteDir()
    {
        Assert.Equal(string.Empty, MsgRows.PoolKey(string.Empty));
        Assert.Equal("foo/bar", MsgRows.PoolKey("foo/bar"));

        // the negative: an absolute spelling is still resolved and trimmed, not passed through
        var real = Directory.CreateDirectory(Path.Join(_base, "abs")).FullName;
        var link = Path.Join(_base, "abslink");
        _ = Directory.CreateSymbolicLink(link, real);
        Assert.Equal(MsgRows.PoolKey(real), MsgRows.PoolKey(link + "/"));
        Assert.Equal("/", MsgRows.PoolKey("/"));
    }
}
