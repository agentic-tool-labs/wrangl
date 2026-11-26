namespace Ah.Commands;

/// <summary>What <c>roster doctor</c> reads that no other verb does: the directive's status text and the state of a git checkout.</summary>
public interface IRosterDoctor
{
    /// <summary>The status report of the hierarchy at <paramref name="cwd"/>: <c>ah: NOT CONFIGURED</c> and the lines under it.</summary>
    string StatusReport(string cwd);

    /// <summary><c>git status --porcelain</c> in <paramref name="directory"/>; throws <see cref="InvalidOperationException"/> when git cannot answer.</summary>
    string GitPorcelain(string directory);

    /// <summary>
    /// Which design sources for the ux-engineer role are installed, by id: <c>frontend-design</c>, <c>vercel-labs</c>,
    /// <c>jakubkrehel</c>, <c>addyosmani</c>. A source is present when its skill directory exists under the Claude config
    /// directory's <c>skills</c>, or its plugin directory exists under <c>plugins/cache</c>.
    /// </summary>
    IReadOnlyList<string> DesignSources();

    /// <summary>What each of the two trust files says, as the one reader of them reads it.</summary>
    IReadOnlyList<Ah.Abstractions.TrustFileReport> TrustFiles();

    /// <summary>What to tell the user when the repository at <paramref name="cwd"/> has conventions only at ah v1's path, else null.</summary>
    string? ConventionsNote(string cwd);

    /// <summary>The mailbox addresses (<c>name@team</c>) the daemon's live registrations hold. Reads only.</summary>
    IReadOnlyList<string> LiveRegistrationAddresses();

    /// <summary>
    /// The relays that were sent more than ten minutes ago and never shown: one line each, <c>&lt;entry&gt; to &lt;address&gt;, sent
    /// &lt;time&gt;</c>. Reads only.
    /// </summary>
    IReadOnlyList<string> HeldRelays();

    /// <summary>
    /// What to tell the user when the daemon that is running is of an earlier build than this install and so serves mail entries
    /// without regard to their claim records, else null.
    /// </summary>
    string? DaemonRestartNote();

    /// <summary>
    /// What to tell the user when the running daemon did not answer the mail protocol check (in time, without an error, with a
    /// protocol list), else null. A miss can be transient, so it is a warning and not a failed check.
    /// </summary>
    string? DaemonUnansweredNote();

    /// <summary>Whether no daemon lives: the health read never went out and no daemon process or socket exists. Starts none (ah2-616).</summary>
    bool DaemonDown();

    /// <summary>
    /// The read model's counters as one line (<c>name=value</c> pairs) when the daemon is up and keeps a read model, else null: a daemon
    /// that is down, does not answer, or reports none yields no row.
    /// </summary>
    string? DaemonReadModelNote();

    /// <summary>The test-bucket row of <c>roster doctor</c>: <c>ok</c> or <c>warn</c> with the text to show.</summary>
    (string Level, string Detail) TestBucket();

    /// <summary>The <c>ahd-heal</c> row of <c>roster doctor</c>: <c>ok</c>, <c>warn</c> or <c>error</c> with the text to show (heal restarts, the cap, a failed heal). Reads only.</summary>
    (string Level, string Detail) AhdHeal() => ("ok", "not checked in this process");

    /// <summary>The <c>ahd-build</c> row of <c>roster doctor</c>: <c>ok</c> or <c>warn</c> with the install stamp against the plugin, the reason and the fix. Reads only.</summary>
    (string Level, string Detail) AhdBuild() => ("ok", "not checked in this process");

    /// <summary><c>roster doctor --fix</c> for the <c>ahd-build</c> row: starts the rebuild when due; the <c>fix</c> entry, or null for none.</summary>
    string? AhdBuildFix() => null;

    /// <summary>The state-database row of <c>roster doctor</c>: <c>ok</c>, <c>warn</c> or <c>error</c> with the text to show (schema, integrity, backups).</summary>
    (string Level, string Detail) StateDb();
}
