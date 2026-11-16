using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    private const int DueDaemonSeconds = 2;

    /// <summary>
    /// <c>msg due --json</c>: the Claude mod's mail poll as one call. The request object is on stdin and the answer one JSON line on
    /// stdout; <c>daemon.socket</c> names a live daemon that serves <c>/v1/due</c>, so the mod can be pushed mail over it instead of
    /// starting this process every few seconds.
    /// </summary>
    private int Due()
    {
        if (!_opts.IsBare("json"))
        {
            throw _out.Fail("due requires --json");
        }

        string text;
        using (var reader = new StreamReader(_stdin(), new UTF8Encoding(false)))
        {
            text = reader.ReadToEnd();
        }

        JsonValue input;
        try
        {
            input = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            throw _out.Fail("due: stdin is not JSON");
        }

        var due = _graph.MailDue;
        var request = due.ParseRequest(input, out var problem) ?? throw _out.Fail($"due: {problem}");
        var hierarchyDir = input.AsObject().Get("hierarchy_dir") is { IsText: true } hd ? hd.AsString() : null;
        var cwd = input.AsObject().Get("cwd") is { IsText: true } c ? c.AsString() : _cwd;
        var offByEnvironment = _env.GetVariable("AH_DAEMON") == "off";
        var source = hierarchyDir is null || offByEnvironment ? null : new DaemonBoxes(_graph.Daemon, request, cwd, hierarchyDir);
        var answer = due.Compute(request, source);
        _out.Json(due.AnswerJson(answer, offByEnvironment ? null : DuePushSocket()));
        return 0;
    }

    /// <summary>The socket of a live daemon that serves <c>GET /v1/due</c>, else null. An older daemon answers that path with 404.</summary>
    private string? DuePushSocket()
    {
        var info = _graph.Daemon.DiscoverAsync(TimeSpan.FromSeconds(DueDaemonSeconds)).GetAwaiter().GetResult();
        if (info is null)
        {
            return null;
        }

        var probe = _graph.Daemon
            .RequestAsync(new DaemonRequest("GET", MeshModule.DueProbePath, default, TimeSpan.FromSeconds(DueDaemonSeconds), StartIfDown: false))
            .GetAwaiter()
            .GetResult();
        return probe.Status == 400 ? info.Socket : null;
    }

    /// <summary>The daemon's <c>/v1/inbox</c> as the box reader: the same query the mod sent, never starting a daemon.</summary>
    private sealed class DaemonBoxes(IDaemonClient daemon, DueRequest request, string cwd, string hierarchyDir) : IDueBoxSource
    {
        public DueBoxRead? Read(string address, string dir, long since)
        {
            var path = "/v1/inbox?address=" + Uri.EscapeDataString(address)
                + "&adapter=claude-mod&session_id=" + Uri.EscapeDataString(request.Session)
                + "&poll_ms=" + request.PollMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&since=" + since.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&wait=0&cwd=" + Uri.EscapeDataString(cwd)
                + "&hierarchy_dir=" + Uri.EscapeDataString(hierarchyDir);
            var reply = daemon
                .RequestAsync(new DaemonRequest("GET", path, default, TimeSpan.FromSeconds(DueDaemonSeconds), StartIfDown: false))
                .GetAwaiter()
                .GetResult();
            if (reply.Status != 200 || !JsonText.TryParse(reply.Body.Span, out var body) || !body.IsMap)
            {
                return null;
            }

            if (body.AsObject().Get("entries") is not { IsArray: true } entries
                || body.AsObject().Get("next") is not { IsNumber: true } next)
            {
                return null;
            }

            return new DueBoxRead([.. entries.AsArray()], (long)next.AsNumber());
        }
    }
}
