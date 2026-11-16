using System.Diagnostics;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Cli.Verbs;

/// <summary><c>ah alarm test</c>: runs the user's <c>alarm.command</c> once, in-process, through the runner the stall watch uses. No episode is touched.</summary>
internal static class AlarmVerb
{
    private const string Usage = "usage: ah alarm test [--team T] [--cwd DIR]";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? team = null;
        var ok = args.Length > 0 && args[0] == "test";
        for (var i = 1; ok && i < args.Length; i += 2)
        {
            ok = args[i] is "--team" or "--cwd" && i + 1 < args.Length;
            if (ok && args[i] == "--team")
            {
                team = args[i + 1];
            }
        }

        if (!ok)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah alarm: the services have not been composed");
            return AhdCtl.AhdCtlVerb.NotComposed;
        }

        var g = services.Value;
        var host = new StallHost(g, context.Env.AhHome, () => true, () => null);
        var at = g.Io.Clock.UtcNow;
        var channel = host.AlarmChannel();
        var clock = Stopwatch.StartNew();
        var exit = host.RunAlarmCommand($"ah: alarm test from {System.Environment.MachineName} at {Timestamps.ToIsoString(at)}.");
        var ms = clock.ElapsedMilliseconds;
        if (exit is not { } code)
        {
            stderr.WriteLine($"ah alarm: alarm.command is not set in {g.Config.UserConfigPath()} and herdr is not on PATH");
            return 1;
        }

        host.Journal(new JsonObject
        {
            ["at"] = Timestamps.ToIsoString(at),
            ["team"] = team is null ? JsonValue.Null : team,
            ["kind"] = "test",
            ["delivered_to"] = channel,
            ["exit"] = code,
        });
        if (code == 0)
        {
            stdout.WriteLine($"{channel} exited 0 in {ms} ms");
            return 0;
        }

        stderr.WriteLine(code == -1 ? $"ah alarm: {channel} timed out or did not start ({ms} ms)" : $"ah alarm: {channel} exited {code} in {ms} ms");
        return 1;
    }
}
