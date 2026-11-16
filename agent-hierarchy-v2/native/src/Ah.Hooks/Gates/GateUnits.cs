using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// The native hook units of the PreToolUse gates, by the script name <c>hooks.json</c> registers, each with the tools it runs for.
/// The pretooluse-chain unit runs the gates whose matcher fits the tool, in this order.
/// </summary>
internal static class GateUnits
{
    private const string Dispatch = "Agent|Task|SendMessage";

    public static IReadOnlyList<GateUnit> All { get; } =
    [
        new("pretooluse-soft-rules", SoftRules.Run, GateClass.Advisory, SoftRules.ConduitFires, "*"),
        new("pretooluse-ah-cli", AhCliGate.Run, GateClass.Split, AhCliGate.StaysHard, "Bash"),
        new("pretooluse-compound-allow", CompoundAllowGate.Run, GateClass.Split, CompoundAllowGate.StaysHard, "Bash"),
        new("pretooluse-mail-claim", MailClaimGate.Run, GateClass.Advisory, Matcher: Dispatch),
        new("pretooluse-msg-gate", AutoTrack.Run, GateClass.Advisory, Matcher: Dispatch),
        new("pretooluse-sendmessage-response", SendMessageResponseGate.Run, GateClass.Advisory, Matcher: Dispatch),
    ];
}
