using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>Whether a member's session is running, as the stall watch and the stale sweeper both read it.</summary>
public enum Verdict
{
    Live,
    Dead,
    Unknown,
}

/// <summary>A verdict and the evidence for it.</summary>
public sealed record MemberVerdict(Verdict Status, string Why);

/// <summary>What herdr says about the pane of a member: its agent status and agent type, or that it could not be asked.</summary>
public sealed record PaneProbe(bool Indeterminate, string? AgentStatus, string? AgentType, string Why);

/// <summary>
/// The one reading of "is this member running". The registration (process identity) decides first. Herdr is asked only for a member
/// the registration does not call live and that has a pane; it makes the member live only when it reports a running agent that is
/// not a shell, because a pane whose agent exited can fall back to a shell that herdr still lists.
/// </summary>
internal static class MemberVerdicts
{
    internal static MemberVerdict Judge(MemberRegistrationLiveness? registration, string? paneId, Func<PaneProbe>? probe, int? startingAgeSeconds = null)
    {
        if (registration is { Live: true })
        {
            return new MemberVerdict(Verdict.Live, registration.Why);
        }

        (LivenessVerdict, string) unchanged = registration switch
        {
            null => (LivenessVerdict.Unknown, "no registration names the member"),
            { ProvenDead: true } => (LivenessVerdict.ProvenDead, registration.Why),
            _ => (LivenessVerdict.Unknown, registration.Why),
        };
        var (verdict, why) = LivenessRules.Settle(unchanged, string.IsNullOrEmpty(paneId) || probe is null ? null : PaneWhy(probe()), startingAgeSeconds);
        return new MemberVerdict(verdict switch { LivenessVerdict.Live => Verdict.Live, LivenessVerdict.ProvenDead => Verdict.Dead, _ => Verdict.Unknown }, why);
    }

    private static string? PaneWhy(PaneProbe pane) =>
        !pane.Indeterminate && LivenessRules.PaneShowsAgent(pane.AgentStatus, pane.AgentType)
            ? $"pane shows an agent: herdr reports a {pane.AgentType ?? "running"} agent {pane.AgentStatus}"
            : null;
}
