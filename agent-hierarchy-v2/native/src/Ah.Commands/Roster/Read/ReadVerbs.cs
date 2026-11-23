using Ah.Abstractions;

namespace Ah.Commands;

/// <summary>The roster CLI's identity and read verbs: <c>roster whoami|teams|history|status|checkin|adopt</c>.</summary>
internal static class ReadVerbs
{
    [Verb("roster:whoami")]
    internal static int Whoami(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, WhoamiVerb.Run);

    [Verb("roster:teams")]
    internal static int Teams(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, TeamsVerb.Run);

    [Verb("roster:team")]
    internal static int Team(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, TeamShowVerb.Run);

    [Verb("roster:history")]
    internal static int History(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, HistoryVerb.Run);

    [Verb("roster:status")]
    internal static int Status(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StatusVerb.Run);

    [Verb("roster:checkin")]
    internal static int Checkin(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, CheckinVerb.Run);

    [Verb("roster:adopt")]
    internal static int Adopt(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, AdoptVerb.Run);

    [Verb("roster:wake")]
    internal static int Wake(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, WakeVerb.Run);

    [Verb("roster:overdue")]
    internal static int Overdue(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, OverdueVerb.Run);

    [Verb("roster:checklist")]
    internal static int Checklist(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ChecklistVerb.Run);

    [Verb("roster:timing")]
    internal static int Timing(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, TimingVerb.Run);

    [Verb("roster:ledger")]
    internal static int Ledger(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, LedgerVerb.Run);

    [Verb("roster:job")]
    internal static int Job(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, JobVerb.Run);

    [Verb("roster:resource")]
    internal static int Resource(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ResourceVerb.Run);

    [Verb("roster:stall-diagnose")]
    internal static int StallDiagnose(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StallDiagnoseVerb.Run);

    [Verb("roster:recover")]
    internal static int Recover(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, RecoverVerb.Run);

    [Verb("roster:allow")]
    internal static int Allow(string[] rest, TextWriter stdout, TextWriter stderr) => Removed("allow", stdout);

    [Verb("roster:revoke")]
    internal static int Revoke(string[] rest, TextWriter stdout, TextWriter stderr) => Removed("revoke", stdout);

    /// <summary>A verb ah-simplify removed: it says so and exits 0, for one release.</summary>
    private static int Removed(string verb, TextWriter stdout)
    {
        stdout.WriteLine($"ah roster {verb}: this command was removed (there are no grants or gate modes any more; nothing to do)");
        return 0;
    }

    [Verb("roster:block-triage")]
    internal static int BlockTriageVerb(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, BlockTriage.Run);

    [Verb("roster:gate")]
    internal static int Gate(string[] rest, TextWriter stdout, TextWriter stderr) => Removed("gate", stdout);
}
