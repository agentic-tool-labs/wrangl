using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Platform.Tests;

/// <summary>A newer-schema refusal is logged once; every other error each time.</summary>
public sealed class HookErrorLogTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ah-hook-log").FullName;

    public void Dispose() => Directory.Delete(_home, true);

    private int Rows(params string[] messages)
    {
        var env = PlatformModule.CreateEnvironment(_ => null, null, new FileReader(), 501, () => _home);
        var log = new HookErrorLog(PlatformModule.CreateIo(null).Clock, env, new FileReader(), new RawFileWriter(), new RecordAppender());
        foreach (var message in messages)
        {
            log.Log(new HookErrorRecord("h", message));
        }

        return File.ReadAllLines(AhPaths.HookErrorLog(env.HomeDirectory)).Length;
    }

    [Fact]
    public void ANewerSchemaRefusalIsLoggedOnce()
    {
        const string Refusal = "the state database is schema 18, newer than this binary's 17; update ah";

        Assert.Equal(1, Rows(Refusal, Refusal, Refusal));
    }

    [Fact]
    public void AnyOtherRepeatedErrorIsLoggedEachTime()
    {
        Assert.Equal(2, Rows("boom", "boom"));
    }
}
