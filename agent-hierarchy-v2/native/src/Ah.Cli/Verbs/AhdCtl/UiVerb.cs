using System.Collections;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Ui;
using Ah.Platform;

namespace Ah.Cli.Verbs.AhdCtl;

/// <summary>
/// <c>ahd ui [--print] [--no-open]</c>: asks the daemon, over its socket, for a lease on the loopback browser bridge, opens the page
/// it names and holds the lease until it is interrupted or the daemon ends it. The URL is
/// printed only with <c>--print</c> (which <c>--no-open</c> implies), and also when there is no <c>open</c> or <c>xdg-open</c>
/// to hand it to. Exit codes: 0 a clean close, 2 the daemon is unreachable (or a usage error), 3 the daemon refuses the lease.
/// </summary>
internal static class UiVerb
{
    internal const int UsageError = 2;

    internal const int Unreachable = 2;

    internal const int Refused = 3;

    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DaemonBudget = TimeSpan.FromSeconds(10);

    /// <summary>What the verb needs from outside it, so a test can stand in for the daemon, the browser and the keyboard.</summary>
    /// <param name="FindSocket">The daemon's socket path, or null when there is no reachable daemon.</param>
    /// <param name="OpenLease">Requests the lease over the socket and returns once its first line has arrived.</param>
    /// <param name="OpenBrowser">Hands the URL to the system opener; false when there is none, or it failed.</param>
    /// <param name="OpenServiceLease">Requests the lease that `ahd ui start` keeps, which the idle stop does not end; null where a test needs none.</param>
    internal sealed record Seams(
        Func<CancellationToken, Task<string?>> FindSocket,
        Func<string, CancellationToken, Task<IHeldReply>> OpenLease,
        Func<string, bool> OpenBrowser,
        Func<string, CancellationToken, Task<IHeldReply>>? OpenServiceLease = null);

    /// <summary>Runs the verb with the process's own services and a stop on SIGINT or SIGTERM.</summary>
    internal static int Run(CliContext context, IDaemonClient daemon, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            return Ah.DaemonClient.DaemonClientModule.Lifecycle is { } lifecycle
                ? lifecycle.RunAsync("status", ["--help"], stdout, stderr).GetAwaiter().GetResult()
                : AhdCtlVerb.NotComposed;
        }

        using var stop = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal => Stop(signal, stop));
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => Stop(signal, stop));
        var seams = new Seams(
            async cancellation => (await daemon.EnsureDaemonAsync(DaemonBudget, cancellation).ConfigureAwait(false))?.Socket,
            (socket, cancellation) => PlatformModule.OpenHeldAsync(socket, "POST", "/v1/ui/lease", BuildInfo.Version, LeaseTimeout, cancellation),
            OpenWithSystem,
            (socket, cancellation) => PlatformModule.OpenHeldAsync(socket, "POST", "/v1/ui/lease?service=1", BuildInfo.Version, LeaseTimeout, cancellation));
        return args.Count > 0 && UiServiceVerb.Handles(args[0])
            ? UiServiceVerb.RunAsync(args, stdout, stderr, seams, UiServiceVerb.RealSeams(context, CurrentEnvironment()), stop.Token).GetAwaiter().GetResult()
            : RunAsync(args, stdout, stderr, seams, stop.Token).GetAwaiter().GetResult();
    }

    private static void Stop(PosixSignalContext signal, CancellationTokenSource stop)
    {
        signal.Cancel = true;
        stop.Cancel();
    }

    /// <summary>The verb itself. <paramref name="stop"/> is the user's interrupt.</summary>
    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, Seams seams, CancellationToken stop)
    {
        var print = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--print":
                case "--no-open":
                    print = true;
                    break;
                default:
                    stderr.WriteLine($"ahd ui: unknown argument {JsonText.Stringify(arg, 0)}; usage: ahd ui [--print] [--no-open] | ahd ui start|restart [--no-open] [--json] | ahd ui stop [--json] | ahd ui status [--json]");
                    return UsageError;
            }
        }

        IHeldReply held;
        try
        {
            if (await seams.FindSocket(stop).ConfigureAwait(false) is not { } socket)
            {
                stderr.WriteLine("ahd ui: the daemon is not reachable");
                return Unreachable;
            }

            held = await seams.OpenLease(socket, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or SocketException or OperationCanceledException)
        {
            stderr.WriteLine($"ahd ui: the daemon is not reachable ({e.GetType().Name})");
            return Unreachable;
        }

        await using (held.ConfigureAwait(false))
        {
            if (held.Status != 200)
            {
                stderr.WriteLine($"ahd ui: {RefusalOf(held)}");
                return Refused;
            }

            if (!TryParseGrant(held.FirstLine.Span, out var port))
            {
                stderr.WriteLine("ahd ui: the daemon's answer could not be read");
                return Refused;
            }

            if (MovedNote(held.FirstLine.Span, port) is { } moved)
            {
                stderr.WriteLine(moved);
            }

            var url = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/ui/";
            if (!print && !seams.OpenBrowser(url))
            {
                print = true;
            }

            stderr.WriteLine($"ahd ui: web UI at http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/ui/, served while this runs; press Ctrl-C to close.");
            stdout.WriteLine(print ? url : $"ah ui: opened in your browser (port {port.ToString(CultureInfo.InvariantCulture)}); Ctrl-C closes it");
            await stdout.FlushAsync(stop).ConfigureAwait(false);

            var interrupted = new TaskCompletionSource();
            await using var registration = stop.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), interrupted);
            if (await Task.WhenAny(held.Ended, interrupted.Task).ConfigureAwait(false) == held.Ended && !stop.IsCancellationRequested)
            {
                stderr.WriteLine("ahd ui: the daemon closed the page");
            }

            return 0;
        }
    }

    internal static bool TryParseGrant(ReadOnlySpan<byte> line, out int port)
    {
        port = 0;
        if (!JsonText.TryParse(line, out var parsed) || parsed.Kind != JsonKind.Map)
        {
            return false;
        }

        var doc = parsed.AsObject();
        if (!doc.TryGetValue("port", out var p) || p.Kind != JsonKind.Number || !double.IsInteger(p.AsNumber()) || p.AsNumber() is < 1 or > 65535)
        {
            return false;
        }

        port = (int)p.AsNumber();
        return true;
    }

    /// <summary>The one line for a listener that could not take its remembered port, or null. Browser storage is per address, so this is why settings look missing.</summary>
    internal static string? MovedNote(ReadOnlySpan<byte> line, int port) =>
        JsonText.TryParse(line, out var parsed) && parsed.IsMap && parsed.AsObject().Get("moved_from") is { IsNumber: true } from
            ? $"ahd ui: port {((int)from.AsNumber()).ToString(CultureInfo.InvariantCulture)} is taken, so the UI is on {port.ToString(CultureInfo.InvariantCulture)}; settings this browser keeps per address (theme, filters, zoom) will look missing there. Saved looks are safe."
            : null;

    internal static string RefusalOf(IHeldReply reply)
    {
        if (JsonText.TryParse(reply.FirstLine.Span, out var parsed) && parsed.Kind == JsonKind.Map
            && parsed.AsObject().TryGetValue("error", out var error) && error.Kind == JsonKind.Map
            && error.AsObject().Get("message") is { IsText: true } message && message.AsString().Length > 0)
        {
            var text = message.AsString();
            return text.Any(static c => char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) ? SafeText.Render(text) : text;
        }

        return $"the daemon refused the lease (HTTP {reply.Status.ToString(CultureInfo.InvariantCulture)})";
    }

    private static Dictionary<string, string?> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        return environment;
    }

    private static bool OpenWithSystem(string url) =>
        OpenWithSystem(PlatformModule.CreateDetachedSpawner(), CurrentEnvironment(), SystemOpener.Name, url);

    /// <summary>Hands <paramref name="url"/> to <paramref name="opener"/>; see <see cref="SystemOpener.Open"/>.</summary>
    internal static bool OpenWithSystem(IDetachedSpawner spawner, IReadOnlyDictionary<string, string?> environment, string opener, string url) =>
        SystemOpener.Open(spawner, environment, opener, url);
}
