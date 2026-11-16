using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// <c>scratch-sweep</c>: list the member scratch that is past its retention (the default, read-only, with the bytes it would free)
/// or, with <c>--commit</c>, delete it. The daemon's job runs the same <see cref="ScratchSweep"/> with commit.
/// </summary>
internal static class ScratchSweepVerb
{
    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null || c.Services.Scratch is not { } scratch)
        {
            c.Stderr.WriteLine("ah: roster scratch-sweep is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("commit" or "cwd"))
            {
                throw new RosterFailure($"scratch-sweep: unrecognized flag --{key} (use --commit)");
            }
        }

        var commit = c.Opts.IsTrue("commit");
        var report = LaunchModule.SweepScratch(scratch, c.Services.Launch.Teardown, c.Services.Roster, c.Services.Files, (op, args) => c.Services.Hierarchy.StateCalls.Grants(op, args), c.Dir, c.TeamHome, c.Services.Clock.UtcNow, commit);
        var entries = new JsonArray();
        foreach (var entry in report.Entries)
        {
            entries.Add(new JsonObject { ["path"] = entry.Path, ["bytes"] = JsonValue.FromNumber(entry.Bytes) });
        }

        c.Out(new JsonObject
        {
            ["committed"] = report.Deleted,
            ["entries"] = entries,
            ["count"] = JsonValue.FromNumber(report.Entries.Count),
            ["bytes"] = JsonValue.FromNumber(report.Bytes),
            ["retention_hours"] = JsonValue.FromNumber(LaunchModule.ScratchRetention.TotalHours),
        });
        return 0;
    }
}
