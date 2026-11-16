using Ah.Core.Json;
using Ah.Launch;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// What the verbs that type into, or read from, a member's pane run on beyond the launch layer: the multiplexer runner for tmux,
/// the paste path, the screen readers and rules, the mesh gate, and the paste attempt log and lock. The composition root builds
/// one when the first such verb asks for it.
/// </summary>
public interface IRosterPanes
{
    /// <summary>The one place a multiplexer binary is run; <c>tmux</c> is reached through it.</summary>
    IMuxRunner Mux { get; }

    /// <summary>The hardened paste path, and where a member's recorded placement says it runs.</summary>
    IPaste Paste { get; }

    /// <summary>What a screen shows and the answers a prompt takes.</summary>
    IScreenRules Screens { get; }

    /// <summary>One read of a member's screen, and its excerpts.</summary>
    IScreenReading Reading { get; }

    /// <summary>What Codex's own configuration says about trusting a directory.</summary>
    ICodexTrust Trust { get; }

    /// <summary>The peer mesh's send rule.</summary>
    IMeshGate Mesh { get; }

    /// <summary>Waits between two looks at a member.</summary>
    ISleeper Sleeper { get; }

    /// <summary>Appends one row to the paste attempt log, for an explicit deliver that ended before anything was typed.</summary>
    void RecordDeliverAttempt(JsonObject row);

    /// <summary>
    /// The per-member paste lock every typist takes, so two never overlap; waits as long as a paste does. Null when it never
    /// cleared. Dispose to release.
    /// </summary>
    IDisposable? HoldPasteLock(string teamName, string memberName);
}
