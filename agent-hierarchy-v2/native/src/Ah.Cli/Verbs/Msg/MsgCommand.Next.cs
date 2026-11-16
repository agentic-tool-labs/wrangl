using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>
    /// <c>msg next [--team T] [--peek] [--again] [--all]</c>: pulls the caller's mail one item at a time, or with <c>--all</c> every queued item in order. The session is the one this process
    /// provably holds; <c>--team</c> names a team whose <c>orchestrator@</c> box the caller owns, so nobody drains a box it does not own.
    /// </summary>
    private int MailNext()
    {
        var session = WritingSession ?? throw new MailFailure("unknown_address", "cannot tell which session this is; run `ah roster checkin` in this session to re-register");
        var team = Str("team");
        if (team is { Length: > 0 } && !Me().Any(b => b.Address == "orchestrator@" + team))
        {
            throw new MailFailure("not_your_session", $"{SafeText.Render(team)} is not a team whose orchestrator box this session owns");
        }

        var now = _graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
        var teamArg = team is { Length: > 0 } ? team : null;
        var result = _opts.IsBare("all") && !_opts.IsBare("again")
            ? _graph.MailDoorbell.PullAll(session, now, _opts.IsBare("peek"), teamArg)
            : _graph.MailDoorbell.Pull(session, now, _opts.IsBare("peek"), _opts.IsBare("again"), teamArg);
        var json = new JsonObject { ["empty"] = result.Empty, ["left"] = (double)result.Left, ["new"] = (double)result.New, ["text"] = result.Text };
        if (!_opts.IsBare("json") && result.Text.Length == 0)
        {
            return 0;
        }

        return Print(json, result.Text, 0);
    }
}
