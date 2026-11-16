using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>The most hops a forwarded entry may carry.</summary>
    private const int MaxForwardHops = 4;

    /// <summary>
    /// <c>ah msg forward &lt;entry id&gt; --to &lt;addr&gt; [--note &lt;text&gt; | --note-ref &lt;path&gt;]</c>: an orchestrate-class session passes an entry
    /// of its own box on, with the original's payload and a hop chain, and acks the original for itself. The send goes through MeshRule as any send does.
    /// </summary>
    private int MailForward()
    {
        if (_opts.Positional.Count != 1 || Str("to") is not { Length: > 0 } to)
        {
            throw new MailFailure("usage", "forward needs one entry id and --to <addr>: ah msg forward <entry id> --to <addr> [--note <text> | --note-ref <path>]");
        }

        var note = Str("note");
        var noteRef = Str("note-ref");
        if (note is not null && noteRef is not null)
        {
            throw new MailFailure("usage", "forward takes --note or --note-ref, not both");
        }

        var id = _opts.Positional[0];
        var mailbox = _graph.Mailbox;
        var ctx = _graph.Callers.RequestContext(Where);
        var boxes = Me(write: true).Where(b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)
            || (mailbox.ResolveAddress(b.Address, ctx).Value?.Member?.Member("role") is { IsText: true } role && Ah.Config.ConfigModule.BuiltinRoleClass(role.AsString()) == "orchestrate")).ToList();
        if (boxes.Count == 0)
        {
            throw new MailFailure("forward_not_orchestrator", "only an orchestrate-class session forwards; a member replies instead");
        }

        var (box, original) = boxes.SelectMany(b => mailbox.ReadEntries(b.Dir).Entries.Where(e => e.IsMap && e.Member("id") is { IsText: true } t && t.AsString() == id).Select(e => (b, e.AsObject())))
            .FirstOrDefault();
        if (original is null)
        {
            throw new MailFailure("not_yours", $"no entry {id} in a box of this session");
        }

        var target = Unwrap(mailbox.ResolveAddress(to, ctx));
        var prior = original.Get("fwd") is { IsMap: true } f ? f.AsObject() : null;
        var origin = prior?.Get("origin") ?? original.Get("from") ?? "user";
        var hops = new JsonArray();
        if (prior?.Get("hops") is { IsArray: true } h)
        {
            hops.AddRange(h.AsArray());
        }

        var hop = new JsonObject { ["by"] = box.Address, ["at"] = Timestamps.ToIsoString(_graph.Io.Clock.UtcNow) };
        if (note is not null)
        {
            hop["note"] = note;
        }
        else if (noteRef is not null)
        {
            hop["note_ref"] = Path.GetFullPath(noteRef, _cwd);
        }

        hops.Add(hop);
        // the new hop counts: forwarding to oneself is a cycle too
        if (origin.IsText && origin.AsString() == target.Address || hops.Any(x => x.Member("by") is { IsText: true } by && by.AsString() == target.Address))
        {
            throw new MailFailure("forward_cycle", $"{target.Address} is already on the forward chain of {id}");
        }

        if (hops.Count > MaxForwardHops)
        {
            throw new MailFailure("forward_hops", $"{id} has been forwarded {MaxForwardHops} times; that is the limit");
        }

        var request = new JsonObject
        {
            ["to"] = target.Address,
            ["from"] = box.Address,
            ["session_id"] = WritingSession is { } sid ? sid : JsonValue.Null,
            ["id"] = mailbox.NewId(),
            ["fwd"] = new JsonObject { ["of"] = prior?.Get("of") ?? id, ["origin"] = origin, ["hops"] = hops },
        };
        AddContext(request, ctx);
        foreach (var key in (string[])["kind", "ref", "ref_sha256", "text", "event"])
        {
            if (original.Get(key) is { IsNull: false } v)
            {
                request[key] = v;
            }
        }

        // ponytail: the files path, not the daemon: /v1/send keeps only its contract's send_keys, which has no `fwd`
        var sent = Unwrap(_graph.Sender.Send(request, null, TimeSpan.FromMilliseconds(LockWaitMs)));
        RecordRelayObligation(sent, target.Dir);
        _ = mailbox.AckIds(box.Dir, [id], "cli", SessionId);
        sent["forwarded"] = id;
        return Print(sent, $"forwarded {id} to {target.Address}; acked {id}\n{mailbox.RenderSend(sent)}", 0);
    }
}
