using Ah.Abstractions;

namespace Ah.Commands;

/// <summary>The team-lifecycle verbs of the roster CLI: each hands its arguments and its body to the shared layer.</summary>
internal static class TeamVerbs
{
    [Verb("roster:create")]
    internal static int Create(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, CreateVerb.Run);

    [Verb("roster:spawn-one")]
    internal static int SpawnOne(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Commands.SpawnOne.RunSpawnOne);

    [Verb("roster:spawn-ad-hoc")]
    internal static int SpawnAdHoc(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Commands.SpawnOne.RunSpawnAdHoc);

    [Verb("roster:confirm-registration")]
    internal static int ConfirmRegistration(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ConfirmRegistrationVerb.Run);

    [Verb("roster:disband")]
    internal static int Disband(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, DisbandVerb.Run);

    [Verb("roster:dismiss")]
    internal static int Dismiss(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, DismissVerb.Run);

    [Verb("roster:untrack")]
    internal static int Untrack(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, UntrackVerb.Run);

    [Verb("roster:reap")]
    internal static int Reap(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ReapVerb.Run);

    [Verb("roster:close")]
    internal static int Close(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, static c => TeamCloseVerb.Client(c, reopen: false));

    [Verb("roster:reopen")]
    internal static int Reopen(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, static c => TeamCloseVerb.Client(c, reopen: true));

    [Verb("roster:snapshot")]
    internal static int Snapshot(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, SnapshotVerb.Run);

    [Verb("roster:resync")]
    internal static int Resync(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ResyncVerb.Run);

    [Verb("roster:move")]
    internal static int Move(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, MoveVerb.Run);

    [Verb("roster:transfer")]
    internal static int Transfer(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, TransferVerb.Run);

    [Verb("roster:next-split")]
    internal static int NextSplit(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, LayoutVerbs.NextSplit);

    [Verb("roster:layout-splits")]
    internal static int LayoutSplits(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, LayoutVerbs.LayoutSplits);

    [Verb("roster:scratch-sweep")]
    internal static int ScratchSweepCommand(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ScratchSweepVerb.Run);

    [Verb("roster:stream-open")]
    internal static int StreamOpen(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StreamVerbs.Open);

    [Verb("roster:stream-done")]
    internal static int StreamDone(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StreamVerbs.Done);

    [Verb("roster:progress")]
    internal static int Progress(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ProgressItemVerb.Run);

    [Verb("roster:checkpoint")]
    internal static int Checkpoint(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, CheckpointVerb.Run);

    [Verb("roster:quit")]
    internal static int Quit(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, QuitVerb.Run);

    [Verb("roster:pause")]
    internal static int Pause(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, QuitVerb.Run);

    [Verb("roster:stop")]
    internal static int Stop(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, StopVerb.Run);

    [Verb("roster:resume")]
    internal static int Resume(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ResumeVerb.Run);
}
