using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The <c>needs</c> items a plan carries for harness members whose launch directory Codex has not been told to trust: one per trust
/// root, each with the command that grants it. Granting is the user's decision, so the item only names the command; nothing runs it.
/// </summary>
internal static class CodexTrustNeeds
{
    internal const string NeedKind = "codex-trust";

    internal static ICodexTrust TrustOf(RosterContext c) =>
        c.Services.CodexTrust ?? RosterModule.CreateCodexTrust(c.Services.Files, c.Services.Environment, cwd => c.Services.Config.CheckoutRoot(cwd));

    /// <summary>The item for one trust root.</summary>
    internal static JsonObject Item(RosterContext c, string root) => new()
    {
        ["kind"] = NeedKind,
        ["root"] = root,
        ["command"] = $"{c.SelfCommand} codex-trust {RosterContext.ArgWord(root)} --yes",
    };

    /// <summary>The distinct untrusted roots of the non-claude, non-custom <paramref name="members"/> launched in <paramref name="launchCwd"/> (default: the cwd).</summary>
    internal static JsonArray For(RosterContext c, IEnumerable<JsonValue> members, string? launchCwd = null)
    {
        var needs = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var launch = c.Services.Launch;
        if (launch is null)
        {
            return needs;
        }

        var trust = TrustOf(c);
        foreach (var member in members)
        {
            var kind = MemberPlanning.KindOf(c, MemberPlanning.Obj(member));
            if (kind == "claude" || kind == launch.Registry.KindCustom)
            {
                continue;
            }

            var check = trust.Check(launchCwd ?? c.Cwd);
            if (MemberPlanning.Text(check, "verdict") == "trusted" || MemberPlanning.Text(check, "trustRoot") is not { } root || !seen.Add(root))
            {
                continue;
            }

            needs.Add(Item(c, root));
        }

        return needs;
    }

    /// <summary>Adds <c>needs</c> to <paramref name="output"/> when there is anything to ask.</summary>
    internal static void Attach(JsonObject output, JsonArray needs)
    {
        if (needs.Count > 0)
        {
            output["needs"] = needs;
        }
    }
}
