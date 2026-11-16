using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>Writing a team's snapshot: the document, its file, and the answer both <c>snapshot</c> and a whole-team close give.</summary>
internal static class SnapshotWriter
{
    /// <summary>
    /// Writes the snapshot of team <paramref name="team"/> (null: the default team) in pool <paramref name="dir"/>. Fails when there is
    /// no such team or the write fails.
    /// </summary>
    internal static JsonObject Write(RosterContext c, string dir, string? team)
    {
        var launch = c.Services.Launch!;
        var now = c.Services.Clock.UtcNow;
        var doc = launch.Snapshots.BuildSnapshot(dir, team, now)
            ?? throw new InvalidOperationException($"there is no team {(team is null ? "(the default team)" : $"\"{team}\"")} to snapshot");
        var written = c.Services.Hierarchy.StateCalls.Snapshot(
            dir,
            new JsonObject
            {
                ["team_name"] = launch.SnapshotRules.SnapshotTeamName(team),
                ["ts"] = launch.SnapshotRules.SnapshotStamp(now),
                ["doc"] = doc,
            });
        var members = new JsonArray();
        if (doc.Get("members") is { IsArray: true } list)
        {
            foreach (var m in list.AsArray())
            {
                var row = new JsonObject();
                foreach (var key in new[] { "name", "role", "session_id" })
                {
                    if (m.Member(key) is { } v)
                    {
                        row[key] = v;
                    }
                }

                members.Add(row);
            }
        }

        var file = MemberPlanning.JsConvertText(written.Member("file"));
        var result = new JsonObject
        {
            ["snapshot"] = c.Services.Roster.SnapshotsHome(dir).TrimEnd('/') + "/" + file,
            ["team"] = RosterJson.Text(team),
            ["members"] = members,
        };
        if (written.Member("removed") is { } removed)
        {
            result["removed"] = removed;
        }

        return result;
    }
}

/// <summary><c>snapshot</c>: record the team's launch fields and session ids so <c>resume</c> can bring it back.</summary>
internal static class SnapshotVerb
{
    private static readonly string[] Flags = ["cwd", "team"];

    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster snapshot is not implemented natively: the launch layer is not composed");
            return 70;
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"snapshot: unrecognized flag --{key} (use --team or --cwd)");
            }
        }

        try
        {
            c.Out(SnapshotWriter.Write(c, c.Dir, c.TeamFile));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not RosterFailure)
        {
            throw new RosterFailure($"snapshot: {error.Message}");
        }

        return 0;
    }
}
