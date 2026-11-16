using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>What a shell string came to: <paramref name="Failed"/> is a non-zero exit, a signal, a spawn failure or an output over the cap.</summary>
public sealed record ShellResult(bool Failed, string Stdout, string Stderr, string? ErrorMessage);

/// <summary>Runs a member's launch command: <c>/bin/sh -c &lt;command&gt;</c>, after the hermetic guard has looked at the command string.</summary>
public interface IShellRunner
{
    /// <summary>Runs <paramref name="commandString"/> in <paramref name="workingDirectory"/> and waits for it.</summary>
    ShellResult Run(string commandString, string? workingDirectory);
}

/// <summary>The shell runner over <paramref name="Processes"/>.</summary>
public sealed record ShellRunner(IProcessRunner Processes, IMuxGuard Guard) : IShellRunner
{
    public ShellResult Run(string commandString, string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(commandString);
        Guard.GuardShellCommand(commandString);
        var result = Processes.Run(new ProcessRequest(["/bin/sh", "-c", commandString], workingDirectory, null, default, null, 1 << 20));
        var stdout = Utf8Text.Decode(result.Stdout.Span);
        var stderr = Utf8Text.Decode(result.Stderr.Span);
        if (result.Truncated)
        {
            return new ShellResult(true, stdout, stderr, "stdout maxBuffer length exceeded");
        }

        if (result.ExitCode == 0)
        {
            return new ShellResult(false, stdout, stderr, null);
        }

        return new ShellResult(true, stdout, stderr, $"Command failed: /bin/sh -c {commandString}\n{stderr}");
    }
}

/// <summary>Waits between polls; a fake in a test advances a clock instead of sleeping.</summary>
public interface ISleeper
{
    /// <summary>Blocks for <paramref name="duration"/>.</summary>
    void Sleep(TimeSpan duration);
}

/// <summary>Records what a pane member is doing, in the activity record the status surface reads.</summary>
public interface IActivityRecorder
{
    /// <summary>Records <paramref name="subject"/> (<c>pane-&lt;name&gt;</c>) as idle in the pool <paramref name="dir"/>.</summary>
    void RecordIdle(string dir, string subject);

    /// <summary>Removes <paramref name="subject"/>'s activity record from the pool <paramref name="dir"/>.</summary>
    void Clear(string dir, string subject);
}

/// <summary>The outcome of bringing a member's own Codex app-server up: its thread, or the fixed phrase that names the step that failed.</summary>
public sealed record AppserverBringUp(bool Ok, string? ThreadId, string? Note);

/// <summary>
/// The Codex app-server steps a spawn drives. The server is a detached process that outlives the command and the daemon; the launcher
/// owns its socket directory, process and cleanup, and the bring-up owns the conversation with it.
/// </summary>
public interface IAppserverLauncher
{
    /// <summary>The member's private temp directory, ready, or null when it cannot be made safe.</summary>
    string? EnsureMemberTmp(string tmpDirectory);

    /// <summary>Starts the server with <paramref name="serverArgs"/> in <paramref name="startCwd"/> and waits for <paramref name="socket"/>; its pid, or null when it did not come up.</summary>
    int? Start(string socket, IReadOnlyList<string> serverArgs, string startCwd);

    /// <summary>
    /// Creates the thread on the server just started, gates it, runs the warm-up turn and resumes it, then closes its own connection.
    /// <paramref name="modes"/> is <c>{sandbox, approval}</c>.
    /// </summary>
    AppserverBringUp BringUp(string socket, JsonObject modes, string cwd);

    /// <summary>
    /// Stops the app-server <paramref name="pid"/> that listens on <paramref name="socket"/>, only when the pid is a process id (a whole
    /// number above 1) whose command line is that app-server (a pid can be reused, and a recorded value can be wrong); false, and no
    /// signal sent, when it is not or when its command line cannot be read.
    /// </summary>
    bool Terminate(int pid, string socket);
}

/// <summary>Where the herdr agents list is cached for the life of one command: several readers want one snapshot, and one command costs one exec.</summary>
public interface IHerdrTopology
{
    /// <summary>The live agents (see <see cref="IHerdr.AgentsFromList"/>); <paramref name="fresh"/> re-reads. Throws when herdr cannot answer.</summary>
    JsonArray Query(bool fresh = false);
}

/// <summary>The topology reader: the first answer is kept until a fresh read is asked for.</summary>
public sealed record HerdrTopology(IHerdr Herdr) : IHerdrTopology
{
    private JsonArray? _cache;

    public JsonArray Query(bool fresh = false)
    {
        if (_cache is null || fresh)
        {
            _cache = Herdr.Agents();
        }

        return _cache;
    }
}
