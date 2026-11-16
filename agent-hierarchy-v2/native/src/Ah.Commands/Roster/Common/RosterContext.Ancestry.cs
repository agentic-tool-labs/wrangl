using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Commands;

/// <summary>The caller as the process ancestry proves it: the member marker, the nearest session above the command, and that session's start and Claude session.</summary>
internal sealed record ProvedCaller(bool Marked, NearestSession Nearest, string? PidStart, string? SessionId)
{
    /// <summary>The caller is an Orchestrator or a Claude harness session, and carries no member marker.</summary>
    internal bool Owns => !Marked && Nearest.Kind is NearestKind.Orchestrator or NearestKind.Session;

    /// <summary>For a marked shell: the sub-orchestrator member the ancestry proves it runs under, when its registered row has the orchestrate class.</summary>
    internal SoCarve? Carve { get; init; }

    /// <summary>For a marked shell that names an orchestrate member but whose ancestry proves none: the walk's answer, so the refusal says the owner is unproven.</summary>
    internal bool Unproven { get; init; }

    /// <summary>The caller acts as an owner: an owner session, or a proved sub-orchestrator whose marker agrees with its registration.</summary>
    internal bool Acts => Owns || Carve is { Disagreement: null };

    internal int Pid => Nearest.Pid;
}

internal sealed partial class RosterContext
{
    private ProvedCaller? _proved;

    private bool _soJudged;

    private SoCarve? _so;

    /// <summary>For an unmarked caller the walk calls an Orchestrator: whether it is the registered sub-orchestrator of the target team. Judged once per invocation.</summary>
    internal SoCarve? RegisteredSoOfTarget()
    {
        if (!_soJudged)
        {
            _soJudged = true;
            var proved = Proved();
            _so = !proved.Marked && TeamFile is { } target && Services.OwnerProof is { } proof
                ? SubOrchestratorCarve.RegisteredSo(this, proof, proved.Nearest, target)
                : null;
        }

        return _so;
    }

    /// <summary>
    /// The caller as the process ancestry proves it. The walk and the file reads behind it run once per invocation, on the first
    /// call, and every later call (the invoker, the guard, the owner-record writers) gets the same answer. A member marker ends
    /// the question before any walk.
    /// </summary>
    internal ProvedCaller Proved() => _proved ??= Prove();

    /// <summary>Replaces the proof with one that carries the lane-lead carve an unmarked owner was shown to hold.</summary>
    internal void AdoptProved(ProvedCaller proved) => _proved = proved;

    private ProvedCaller Prove()
    {
        // The daemon's own call acts as no session: its process ancestry is whatever started the daemon, which proves nothing about the team.
        if (DaemonPrincipal)
        {
            return new ProvedCaller(false, new NearestSession(NearestKind.None, null, null, 0), null, null);
        }

        if (EnvText(MeshModule.MemberMarkerEnv) is not null)
        {
            return SubOrchestratorCarve.ProveMarked(this);
        }

        if (Services.OwnerProof is not { } proof)
        {
            return new ProvedCaller(false, new NearestSession(NearestKind.Unavailable), null, null);
        }

        var rowDirs = new[] { Dir, MainDir }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();
        var nearest = proof.NearestAncestor(TeamHome, rowDirs);
        if (SubOrchestratorCarve.FromAncestry(this, nearest) is { } carved)
        {
            return carved;
        }

        if (nearest.Kind is not (NearestKind.Orchestrator or NearestKind.Session))
        {
            return new ProvedCaller(false, nearest, null, null);
        }

        return new ProvedCaller(false, nearest, OrchestratorSession.StartOf(this, nearest.Pid), OrchestratorSession.ProcessTied(this, nearest.Pid));
    }

    /// <summary>The first of <c>--orchestrator-pid</c> and <c>CLAUDE_PID</c> that names a pid other than the proved one, or null. They only cross-check the proof.</summary>
    internal (string Name, string Stated)? StatedPidMismatch()
    {
        var proved = Proved();
        if (!proved.Acts)
        {
            return null;
        }

        foreach (var (name, stated) in new[] { ("--orchestrator-pid", Opts.Text("orchestrator-pid")), ("CLAUDE_PID", EnvText("CLAUDE_PID")) })
        {
            if (stated is not null && JsConvert.ToNumber(JsonValue.FromString(stated)) != proved.Pid)
            {
                return (name, stated);
            }
        }

        return null;
    }

    /// <summary>The pid to record as a team's Orchestrator: the proved one, NaN when none is proved or a stated pid disagrees with it.</summary>
    internal double OwnerPid() => Proved().Acts && StatedPidMismatch() is null ? Proved().Pid : double.NaN;
}
