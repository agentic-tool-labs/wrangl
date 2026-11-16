using Ah.Tui.Adapters;
using Ah.Tui.Core;

namespace Ah.Tui;

/// <summary>What the client runs on: its streams, its environment, whether it has a terminal on both ends, and where views come from.</summary>
internal sealed record TuiHost(
    TextWriter Stdout,
    TextWriter Stderr,
    Func<string, string?> GetVariable,
    bool StdinIsTerminal,
    bool StdoutIsTerminal,
    IViewSource Source,
    IDaemonDiscovery Discovery,
    ITuiTerminal? Terminal = null,
    ITuiClock? Clock = null);

internal static class TuiApp
{
    internal const int NotImplemented = 70;

    private const string Usage =
        "usage: ahd-tui [--team NAME] [--once]\n"
        + "  --team NAME  open on that team\n"
        + "  --once       print one frame and exit\n"
        + "The mouse is on: click a row or a crumb, the wheel scrolls; the terminal's own text selection needs Shift.\n"
        + "Under tmux, set -g mouse on. The full interface is the web UI.\n";

    /// <summary>Runs the client and returns the process exit code.</summary>
    internal static async Task<int> RunAsync(string[] args, TuiHost host, CancellationToken cancellationToken)
    {
        var parsed = TuiArgs.Parse(args);
        if (parsed.Help)
        {
            await host.Stdout.WriteAsync(Usage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (parsed.UsageError is not null)
        {
            var writer = parsed.Plain ? host.Stderr : host.Stdout;
            await writer.WriteAsync(parsed.UsageText.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var caps = TuiCaps.Detect(host.GetVariable);
        var interactive = !parsed.Once && !caps.Dumb && host.StdinIsTerminal && host.StdoutIsTerminal;
        if (interactive)
        {
            if (host.Terminal is null || host.Clock is null)
            {
                await host.Stderr.WriteAsync("ahd-tui: no terminal is available to the interactive view; pass --once\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                return NotImplemented;
            }

            var color = TuiTheme.UseColor(host.GetVariable("NO_COLOR"));
            return await InteractiveTui
                .RunAsync(parsed.Team, host.Terminal, host.Source, host.Discovery, host.Clock, color, host.Stderr, cancellationToken, caps)
                .ConfigureAwait(false);
        }

        return await TuiOnce
            .RunScreenAsync(parsed.Team, host.Source, host.Discovery, host.Clock ?? new SystemTuiClock(), TuiOnce.Columns(host.GetVariable("COLUMNS")), host.Stdout, host.Stderr, cancellationToken)
            .ConfigureAwait(false);
    }
}
