using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>
    /// The caller's box that holds an entry another member handed it over a link (<c>link.from</c> is not the box) whose <c>ref</c> is request
    /// <paramref name="id"/>, or null: only a link's receiver files a verdict on the request (ah2-530). The holder of the request's response slot
    /// (its <c>to</c> role, <paramref name="req"/>'s file name) is never one, also when the receiver's reply came back to it over the link.
    /// </summary>
    private CallerBox? LinkReceiverBox(string id, string req)
    {
        try
        {
            var slot = KeyPart(req, 1);
            return Me(write: true).FirstOrDefault(b => BoxRole(b) is { } role && role != slot && _graph.Mailbox.ReadEntries(b.Dir).Entries.Any(e =>
                e.IsMap && e.Member("link")?.Member("from") is { IsText: true } from && from.AsString() != b.Address
                && e.Member("ref") is { IsText: true } r && MsgPointer.Id(r.AsString()) == id));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or MailFailure)
        {
            // a box that cannot be read received nothing
            return null;
        }
    }

    /// <summary>A fill of a response slot another role holds, from the request's link receiver, names <c>fill --review</c> in its refusal.</summary>
    private T WithReviewHint<T>(string id, string req, Func<T> fill)
    {
        try
        {
            return fill();
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("fill: ", StringComparison.Ordinal) && e.Message.Contains(" is from ", StringComparison.Ordinal)
            && LinkReceiverBox(id, req) is not null)
        {
            var hinted = new InvalidOperationException(
                $"{e.Message}; the response is the other member's: file your verdict with `ah msg fill --id {id} --from {_opts.Text("from")} --review --body-file -`", e);
            foreach (System.Collections.DictionaryEntry d in e.Data)
            {
                hinted.Data[d.Key] = d.Value;
            }

            throw hinted;
        }
    }

    /// <summary>
    /// <c>fill --id &lt;id&gt; --from &lt;role&gt; --review</c>: the link receiver's verdict on request <paramref name="id"/>, stored as the request's note
    /// record (a second one replaces it), then one <c>report</c> entry to the Orchestrator naming the request and the verdict's first line.
    /// </summary>
    private int FillReview(string id, string req, bool reqKnown)
    {
        if (!reqKnown)
        {
            throw _out.Fail($"fill --review: no request {id}");
        }

        if (LinkReceiverBox(id, req) is not { } box)
        {
            throw _out.Fail($"fill --review: {id} was not handed to this session over a link; only its link's receiver files a verdict on it");
        }

        var text = _opts.Has("body-file")
            ? ReadBodyFile(HierarchyModule.FillBodyMax)
            : _opts.Text("body") is { } body && JsonText.Parse(body) is { IsText: true } plain ? plain.AsString() : throw _out.Fail("fill --review: pass the verdict with --body-file or --body '<JSON string>'");
        // the record is named by the receiver's role, also when --from gave its member name
        var ctx = _graph.Callers.RequestContext(Where);
        var role = BoxRole(box) ?? _opts.Text("from")!;
        var res = Hier.StateCalls.FillReview(id, role, req, text);

        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        var team = box.Address[(box.Address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        var report = new JsonObject
        {
            ["to"] = HierarchyModule.OrchestratorAddress(team),
            ["from"] = box.Address,
            ["session_id"] = WritingSession is { } sid ? sid : JsonValue.Null,
            ["id"] = _graph.Mailbox.NewId(),
            ["kind"] = "report",
            ["ref"] = res.Get("path") ?? JsonValue.Null,
            ["verdict"] = $"verdict on {id}: {first}",
        };
        AddContext(report, ctx);
        // ponytail: the files path, not the daemon, as forward does: /v1/send keeps only its contract's send_keys, which has no `verdict`
        var sent = Unwrap(_graph.Sender.Send(report, null, TimeSpan.FromMilliseconds(LockWaitMs)));
        res["type"] = "review";
        res["report"] = sent.Get("entry") is { IsMap: true } entry ? entry.AsObject().Get("id") ?? JsonValue.Null : JsonValue.Null;
        _out.Json(res);
        return 0;
    }
}
