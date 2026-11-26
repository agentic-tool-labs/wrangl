using Ah.Abstractions;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// Single-carrier mail delivery, the sender's side: a SendMessage that carries a mail entry's marker or its request token takes the
/// entry's relay claim before it runs, so an entry the daemon's fallback or another session already holds is never shown a second
/// time. A message that carries an entry's request token without the marker of that entry, the one ah prints for a relay it asks
/// for, is a second carrier of an entry ah delivers itself and is denied whoever it is addressed to. A message that carries neither is left alone; one
/// that does and cannot be recorded is denied, because letting it through would be the second row.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class MailClaimGate
{
    private const string Held = "ah: held a SendMessage for a mail entry that is already delivered or being delivered; see the denial text.";

    public static HookOutcome Run(GateContext c)
    {
        if (c.ToolName() != "SendMessage" || c.Services.Advisory is not { } advisory)
        {
            return HookEmit.Silent;
        }

        var toolInput = c.ToolInput();
        var message = toolInput.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        if (message.Length == 0)
        {
            return HookEmit.Silent;
        }

        var to = toolInput.Get("to") is { IsText: true } t ? t.AsString() : string.Empty;
        try
        {
            if (advisory.MailboxHooks.PointerDuplicate(message, advisory.Callers.RequestContext(c.Cwd())) is { } duplicate)
            {
                return Bounce(c, duplicate);
            }

            // ah2-483 R2: the relay ah printed, for an entry delivered meanwhile, is answered as done: nothing is sent, no NOT DONE, no retry.
            if (advisory.MailboxHooks.PrintedRelayDone(message, c.Input.Text("session_id"), advisory.Callers.RequestContext(c.Cwd())) is { } done)
            {
                return HookEmit.Deny(done, "ah: a printed relay of an entry already delivered was answered as done; nothing was sent.");
            }

            var refused = advisory.MailboxHooks.SendDenial(message, c.Input.Text("session_id"), advisory.Callers.RequestContext(c.Cwd()), out var warning);
            if (refused is not null)
            {
                return Bounce(c, refused);
            }

            var decision = advisory.MailboxHooks.ClaimRelay(c.Input.Text("session_id"), to, message, advisory.Callers.RequestContext(c.Cwd()), c.Input.Text("tool_use_id"));
            if (decision.Deny)
            {
                return Bounce(c, decision.Reason ?? Held);
            }

            return warning is not null ? HookEmit.Warn(warning) : HookEmit.Silent;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            c.LogError(ex);
            return HookEmit.Silent;
        }
    }

    private static HookOutcome Bounce(GateContext c, string reason) =>
        SoftGate.Bounce(c, "mail-claim", reason, "ah delivers this entry itself; do not SendMessage it.", retryPasses: true);
}
