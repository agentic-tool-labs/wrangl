namespace Ah.Commands;

/// <summary>The daemon's answer to whether a new spawn may start now.</summary>
/// <param name="Admitted">Whether the spawn may start.</param>
/// <param name="NextAt">When the next spawn is allowed, as the daemon wrote it; set when not admitted.</param>
/// <param name="Triggers">The load triggers that hold, as text with their values.</param>
/// <param name="Unknown">Why the daemon could not be asked; set when the answer is not known, and then the spawn is not paced.</param>
public sealed record SpawnAdmissionReply(bool Admitted, string? NextAt, IReadOnlyList<string> Triggers, string? Unknown);

/// <summary>The route to the daemon's spawn pacing.</summary>
public interface IRosterSpawnAdmission
{
    /// <summary>Asks whether a spawn of <paramref name="member"/> into <paramref name="team"/> may start; an admitted one is recorded unless <paramref name="dryRun"/>.</summary>
    SpawnAdmissionReply Ask(string team, string member, bool dryRun);
}

/// <summary>
/// While the machine is overloaded the daemon lets one new spawn through per gap; any other is refused with exit 75 and nothing is
/// launched. With no daemon to ask there is no pacing, and the output says the load is unknown.
/// </summary>
internal static class LoadThrottle
{
    /// <summary>The exit code of a spawn the machine load put off.</summary>
    internal const int TempFail = 75;

    /// <summary>Null when the spawn may go on, else the exit code to leave with after the reason was written to stderr.</summary>
    internal static int? Refuse(RosterContext c, string? member)
    {
        if (c.Services.SpawnAdmission is not { } admission)
        {
            return null;
        }

        var reply = admission.Ask(c.Opts.Text("team") ?? c.TeamArg ?? string.Empty, member ?? string.Empty, c.Opts.IsTrue("dry-run"));
        if (reply.Unknown is not null)
        {
            c.Stderr.WriteLine($"ah roster: load unknown ({reply.Unknown}); the spawn is not paced");
            return null;
        }

        if (reply.Admitted)
        {
            return null;
        }

        c.Stderr.WriteLine($"ah roster: machine overloaded ({string.Join("; ", reply.Triggers)}); next spawn allowed after {reply.NextAt}; nothing was launched");
        return TempFail;
    }
}
