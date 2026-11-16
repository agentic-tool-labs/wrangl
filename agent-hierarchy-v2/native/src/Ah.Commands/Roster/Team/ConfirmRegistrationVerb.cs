using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>confirm-registration --member N --pane P --transport T --to R [--wait-ms MS]</c>: the registration wait spawn-one and spawn-ad-hoc
/// no longer make, run detached after their launch. A member that does not register within the wait is mailed to R as
/// <c>member-unconfirmed</c> with the launcher's reason (folder trust, say). Prints nothing.
/// </summary>
internal static class ConfirmRegistrationVerb
{
    /// <summary>The verb's command line for <paramref name="member"/>, run from <paramref name="c"/>'s cwd and team.</summary>
    internal static string CommandLine(RosterContext c, string member, string pane, string transport, string to)
    {
        var words = new List<string> { "confirm-registration", "--member", member, "--pane", pane, "--transport", transport, "--to", to, "--cwd", c.Cwd };
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
        TimeSpan? wait = c.Opts.Text("wait-ms") is { } ms && double.TryParse(ms, out var waitMs) ? TimeSpan.FromMilliseconds(waitMs) : null;
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
        if (MemberPlanning.Text(result, "launch_status") != "unconfirmed")
        {
            return 0;
        }

        var reason = MemberPlanning.JsConvertText(result.Get("unconfirmed_reason"));
        _ = c.Services.EventMail?.SendEvent(
            c.Cwd,
            c.Dir,
            $"member-unconfirmed:{c.ProgressTeam}:{member}:{MemberPlanning.JsConvertText(launched.Get("transport_id"))}",
            "member-unconfirmed",
            $"{member} has not registered — {reason}",
            new JsonObject { ["member"] = member, ["team"] = c.ProgressTeam, ["pane"] = launched.Get("transport_id") ?? JsonValue.Null, ["reason"] = reason },
            [to]);
        return 0;
    }
}
