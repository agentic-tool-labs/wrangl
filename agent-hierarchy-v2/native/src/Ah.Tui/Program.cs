using Ah.DaemonClient;
using Ah.Platform;
using Ah.Tui.Adapters;

namespace Ah.Tui;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var keepCases = Ah.Core.Text.PathGuard.KeepCases();
        var io = PlatformModule.CreateIo(null);
        var transport = PlatformModule.CreateUnixHttpTransport("ahd-tui");
        var runner = PlatformModule.CreateProcessRunner();
        var client = DaemonClientModule.ComposeReadOnly(io.Files, transport, io.Processes, runner, io.Clock, io.Environment);
        var mode = PlatformModule.CreateTerminalMode();
        var source = new DaemonViewSource(client, transport);
        using var terminal = new ConsoleTerminal(mode);
        var host = new TuiHost(
            Console.Out,
            Console.Error,
            Environment.GetEnvironmentVariable,
            mode.IsTerminal(0),
            mode.IsTerminal(1),
            source,
            source,
            terminal,
            new SystemTuiClock());
        using var stop = new StopSignals();
        return await TuiApp.RunAsync(args, host, stop.Token).ConfigureAwait(false);
    }
}
