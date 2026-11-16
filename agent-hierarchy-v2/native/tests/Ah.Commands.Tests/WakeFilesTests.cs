using System.Reflection;
using Ah.Platform;

namespace Ah.Commands.Tests;

/// <summary>Who leaves the wake file when two waiters of one session overlap.</summary>
public sealed class WakeFilesTests
{
    private const int Me = 100;
    private const int Other = 200;
    private const string Name = "s.json";

    private static string OtherBody(string start) => $$"""{"pid":{{Other}},"pid_start":"{{start}}","team":"t","started":"x"}""";

    private static (Waker Wake, string Path) Rig()
    {
        var wake = DispatchProxy.Create<IRosterWake, Waker>();
        var waker = (Waker)(object)wake;
        waker.Self = wake;
        return (waker, WakeFiles.PathOf("/home", Name));
    }

    private static WakeFiles Register(Waker wake) => WakeFiles.Register(wake.Self, PlatformModule.CreateIo(null).Clock, Name, "t");

    [Fact]
    public void AWaiterLeavesAFileAnotherWaiterHasSinceTaken()
    {
        var (wake, path) = Rig();
        var registration = Register(wake);
        wake.Files[path] = OtherBody("running");

        registration.Dispose();

        Assert.Equal(OtherBody("running"), wake.Files[path]);
    }

    [Fact]
    public void AWaiterGivesTheFileBackToTheLiveWaiterItTookItFrom()
    {
        var (wake, path) = Rig();
        wake.Files[path] = OtherBody("running");

        Register(wake).Dispose();

        Assert.Equal(OtherBody("running"), wake.Files[path]);
    }

    [Fact]
    public void AWaiterRemovesTheFileWhenTheWaiterItTookItFromHasGone()
    {
        var (wake, path) = Rig();
        wake.Files[path] = OtherBody("an earlier process");

        Register(wake).Dispose();

        Assert.False(wake.Files.ContainsKey(path));
    }

    public class Waker : DispatchProxy
    {
        public IRosterWake Self { get; set; } = null!;

        public Dictionary<string, string> Files { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_AhHome" => "/home",
            "get_Pid" => Me,
            "StartOf" => (int)args![0]! == Other ? "running" : "mine",
            "ReadText" => Files.GetValueOrDefault((string)args![0]!),
            "WriteText" => Files[(string)args![0]!] = (string)args[1]!,
            "Remove" => Files.Remove((string)args![0]!),
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}
