using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>confirm-registration --member N --pane P --transport T --to R [--spawned-at ISO] [--wait-ms MS]</c>: the registration wait spawn-one and
/// spawn-ad-hoc no longer make, run detached after their launch. A member that has not registered <see cref="Window"/> after its
/// <c>spawned_at</c> (or after <c>--wait-ms</c>), and whose row is still in the team, is mailed to R as <c>member-unconfirmed</c> with the
/// launcher's reason (folder trust, say) and the command to run in its pane. Prints nothing.
/// </summary>
internal static class ConfirmRegistrationVerb
{
    /// <summary>How long after its spawn a member may go unregistered before its spawner is mailed.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>The verb's command line for <paramref name="member"/>, run from <paramref name="c"/>'s cwd and team.</summary>
    internal static string CommandLine(RosterContext c, string member, string pane, string transport, string to, string spawnedAt)
    {
        var words = new List<string> { "confirm-registration", "--member", member, "--pane", pane, "--transport", transport, "--to", to, "--spawned-at", spawnedAt, "--cwd", c.Cwd };
        if (c.TeamArg is { Length: > 0 } team)
        {
            words.AddRange(["--team", team]);
        }

        return c.SelfCommand + " " + string.Join(' ', words.Select(Ah.Core.CoreModule.QuoteShellWord));
    }

    internal static int Run(RosterContext c)
    {
        var launch = c.Services.Launch ?? throw new RosterFailure("confirm-registration: the launch layer is not composed");
        var member = c.Opts.Text("member") ?? throw new RosterFailure("confirm-registration: --member is required");
        var to = c.Opts.Text("to") ?? throw new RosterFailure("confirm-registration: --to is required");
        var transport = c.Opts.Text("transport") ?? "herdr";
        var spawnedAt = c.Opts.Text("spawned-at");
        TimeSpan wait;
        if (c.Opts.Text("wait-ms") is { } ms && double.TryParse(ms, out var waitMs))
        {
            wait = TimeSpan.FromMilliseconds(waitMs);
        }
        else
        {
            var left = spawnedAt is not null && DateTimeOffset.TryParse(spawnedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at + Window - c.Services.Clock.UtcNow
                : Window;
            wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var launched = new JsonObject { ["name"] = member, ["launch_status"] = "ready" };
        if (c.Opts.Text("pane") is { Length: > 0 } pane)
        {
            launched["transport_id"] = pane;
        }

        var result = launch.Launcher.ConfirmRegistration(
            launched,
            transport,
            c.LaunchContext(),
            (name, team) => c.Services.Hierarchy.Core.Liveness is not { } liveness || liveness.ForMember(name, team) is { Live: true },
            wait);
        if (MemberPlanning.Text(result, "launch_status") != "unconfirmed" || !StillMember(c, member, spawnedAt))
        {
            return 0;
        }

        var reason = MemberPlanning.JsConvertText(result.Get("unconfirmed_reason"));
        var paneId = MemberPlanning.JsConvertText(launched.Get("transport_id"));
        _ = c.Services.EventMail?.SendEvent(
            c.Cwd,
            c.Dir,
            $"member-unconfirmed:{c.ProgressTeam}:{member}:{paneId}",
            "member-unconfirmed",
            $"{member} has not registered — {reason}. SendMessage {member} (pane {paneId}) its brief pointer; its next hook registers it and queued mail follows.",
            new JsonObject { ["member"] = member, ["team"] = c.ProgressTeam, ["pane"] = launched.Get("transport_id") ?? JsonValue.Null, ["reason"] = reason },
            [to]);
        return 0;
    }

    /// <summary>Whether the team still holds <paramref name="member"/>'s row (the one spawned at <paramref name="spawnedAt"/> when given): a launch taken back or a member dismissed is not mailed.</summary>
    private static bool StillMember(RosterContext c, string member, string? spawnedAt) =>
        DismissVerb.Members(c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile)).Any(m =>
            m.IsMap && MemberPlanning.Text(m.Member("name")) == member && (spawnedAt is null || MemberPlanning.Text(m.Member("spawned_at")) == spawnedAt));
}
