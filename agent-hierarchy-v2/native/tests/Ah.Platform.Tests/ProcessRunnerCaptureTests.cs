using Ah.Abstractions;

namespace Ah.Platform.Tests;

/// <summary>
/// The output readers keep every byte across many reads of their 8 KiB buffer, in order, on both streams, and stop at the cap
/// (ah2-52: the buffer moved from a rented array to the reader's stack).
/// </summary>
public sealed class ProcessRunnerCaptureTests
{
    // 40 000 bytes per stream: five full buffers and a part, each byte its offset mod 251 so a dropped or repeated read shows
    private const int Size = 40_000;

    private static byte[] Expected(int size) => [.. Enumerable.Range(0, size).Select(static i => (byte)(i % 251))];

    private static ProcessResult Write(int cap)
    {
        var file = Path.Combine(Path.GetTempPath(), "ahcap-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(file, Expected(Size));
        try
        {
            return PlatformModule.CreateProcessRunner().Run(new ProcessRequest(["/bin/sh", "-c", $"cat '{file}'; cat '{file}' >&2"], OutputCapBytes: cap));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void EveryByteOfBothStreamsIsKeptInOrderAndNegativeOnePastTheCapIsNot()
    {
        var whole = Write(1 << 20);
        Assert.False(whole.Truncated);
        Assert.Equal(0, whole.ExitCode);
        Assert.Equal(Expected(Size), whole.Stdout.ToArray());
        Assert.Equal(Expected(Size), whole.Stderr.ToArray());

        var capped = Write(Size - 1);
        Assert.True(capped.Truncated);
        Assert.Equal(Expected(Size - 1), capped.Stdout.ToArray());
    }
}
