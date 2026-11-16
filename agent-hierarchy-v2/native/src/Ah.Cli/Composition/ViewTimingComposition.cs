using Ah.Abstractions;
using Ah.Platform;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>Puts the view-timing decorators between the daemon and its file reader and process runner, and only when the timing is on.</summary>
internal static class ViewTimingComposition
{
    /// <summary>
    /// The services the daemon is built over: when <paramref name="on"/> is false they are returned as they came, with no
    /// decorator anywhere; when it is true the file reader and the process runner count into the current request's scope.
    /// </summary>
    internal static (PlatformIo Io, IProcessRunner Runner) Instrument(PlatformIo io, IProcessRunner runner, bool on) =>
        on ? (io with { Files = WatchModule.TimeFiles(io.Files) }, WatchModule.TimeProcesses(runner)) : (io, runner);
}
