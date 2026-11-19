using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The daemon's answer to whether a new spawn may start now.</summary>
/// <param name="Admitted">Whether the spawn may start.</param>
/// <param name="NextAt">When the next spawn is allowed, as the daemon wrote it; set when not admitted.</param>
/// <param name="Triggers">The load triggers that hold, as text with their values.</param>
/// <param name="Unknown">Why the daemon could not be asked; set when the answer is not known, and then the spawn is not paced.</param>
/// <param name="Harnesses">How many Claude harness processes ran at the daemon's last sample; null when the answer did not say.</param>
public sealed record SpawnAdmissionReply(bool Admitted, string? NextAt, IReadOnlyList<string> Triggers, string? Unknown, int? Harnesses = null);

/// <summary>The route to the daemon's spawn pacing.</summary>
public interface IRosterSpawnAdmission
{
    /// <summary>Asks whether a spawn of <paramref name="member"/> into <paramref name="team"/> may start; an admitted one is recorded unless <paramref name="dryRun"/>.</summary>
    SpawnAdmissionReply Ask(string team, string member, bool dryRun);
}

/// <summary>
/// While the machine is overloaded the daemon lets one new spawn through per gap; any other is refused with exit 75 and nothing is
/// launched. With no daemon to ask there is no pacing, and the output says the load is unknown. The team's own Orchestrator (or the
/// user) may spawn anyway with <c>--despite-load</c> (ah2-263); that spawn is journaled and does not move the pacing clock.
/// </summary>
internal static class LoadThrottle
{
    /// <summary>The exit code of a spawn the machine load put off.</summary>
    internal const int TempFail = 75;

    internal const string DespiteLoadFlag = "despite-load";

    /// <summary>Null when the spawn may go on, else the exit code to leave with after the reason was written to stderr.</summary>
    internal static int? Refuse(RosterContext c, string? member, JsonValue team)
    {
        if (c.Services.SpawnAdmission is not { } admission)
        {
            return null;
        }

        var teamName = c.Opts.Text("team") ?? c.TeamArg ?? string.Empty;
        var dryRun = c.Opts.IsTrue("dry-run");
        var reply = admission.Ask(teamName, member ?? string.Empty, dryRun);
        if (reply.Unknown is not null)
        {
            c.Stderr.WriteLine($"ah roster: load unknown ({reply.Unknown}); the spawn is not paced");
            return null;
        }

        if (reply.Admitted)
        {
            return null;
        }

        var triggers = string.Join("; ", reply.Triggers);
        var owner = c.Services.OwnerProof?.IsUser() == true || DismissVerb.OwningOrchestrator(c, team);
        var despite = c.Opts.IsTrue(DespiteLoadFlag);
        if (owner && despite)
        {
            if (!dryRun)
            {
                c.Stderr.WriteLine($"ah roster: spawned despite machine load ({triggers})");
                var list = new JsonArray();
                list.AddRange(reply.Triggers.Select(JsonValue.FromString));
                c.Services.Wake?.Journal(new JsonObject
                {
                    ["at"] = Ah.Core.Time.Timestamps.ToIsoString(c.Services.Clock.UtcNow),
                    ["kind"] = "load-override",
                    ["team"] = teamName,
                    ["member"] = member ?? string.Empty,
                    ["triggers"] = list,
                });
                c.DespiteLoad = true;
            }

            return null;
        }

        var message = $"ah roster: machine overloaded ({triggers}); next spawn allowed after {reply.NextAt}; nothing was launched";
        if (!owner)
        {
            c.Stderr.WriteLine(message);
            if (despite)
            {
                c.Stderr.WriteLine($"ah roster: --{DespiteLoadFlag} ignored: only the team's Orchestrator can override load pacing");
            }

            return TempFail;
        }

        var harnesses = reply.Harnesses is { } n ? $"; {n} Claude harness processes run" : string.Empty;
        message += $"{harnesses}. To spawn anyway, assess the risk and re-run with --{DespiteLoadFlag}.";
        c.Stderr.WriteLine(message);
        var paced = new JsonArray();
        paced.AddRange(reply.Triggers.Select(JsonValue.FromString));
        var result = new JsonObject
        {
            ["refused"] = "machine-overloaded",
            ["triggers"] = paced,
            ["next_at"] = reply.NextAt is { } next ? next : JsonValue.Null,
            ["message"] = message,
        };
        if (reply.Harnesses is { } count)
        {
            result["harnesses"] = count;
        }

        c.Out(result);
        return TempFail;
    }
}
