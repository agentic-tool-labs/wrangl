using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The daemon's answer to a <c>recover</c> request: its JSON body, or why there is none.</summary>
public sealed record BareRecoverReply(JsonObject? Body, string? Failure);

/// <summary>Reaches the daemon that runs the bare-member recovery; the daemon proves who is asking and keeps all of the state.</summary>
public interface IRosterBareRecover
{
    /// <summary>Asks the daemon to <c>show</c>, <c>hold</c>, <c>release</c> or <c>now</c> for <paramref name="member"/> of <paramref name="team"/> in <paramref name="hierarchyDir"/>.</summary>
    BareRecoverReply Ask(string hierarchyDir, string team, string member, string op);
}
