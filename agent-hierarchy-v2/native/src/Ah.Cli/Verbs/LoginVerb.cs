using System.Globalization;
using System.Text;
using Ah.Cli.Login;
using Ah.Core.Json;

namespace Ah.Cli.Verbs;

internal static class LoginVerb
{
    internal static int Run(CliContext context, string[] rest, TextWriter stdout, TextWriter stderr, Func<int> startDaemon) =>
        Run(context, rest, stdout, stderr, LoginItem.For(context), startDaemon);

    /// <summary>
    /// <c>login on|off|status|run [--plain] [--dry-run]</c>. <c>run</c> is what the login item executes: it starts the daemon
    /// as <c>ahd start</c> does, through <paramref name="startDaemon"/>.
    /// </summary>
    internal static int Run(
        CliContext context, string[] rest, TextWriter stdout, TextWriter stderr, LoginItem? item, Func<int>? startDaemon = null)
    {
        var action = rest.Length > 0 ? rest[0] : string.Empty;
        var plain = false;
        var dryRun = false;
        foreach (var argument in rest.Skip(1))
        {
            switch (argument)
            {
                case "--plain":
                    plain = true;
                    break;
                case "--dry-run" when action is "on" or "off":
                    dryRun = true;
                    break;
                default:
                    stderr.WriteLine($"ahd: login {action} does not accept {argument}");
                    return 2;
            }
        }

        if (action == "run")
        {
            if (startDaemon is null)
            {
                stderr.WriteLine("ahd: login run is not implemented natively");
                return 70;
            }

            return startDaemon();
        }

        if (action is not ("on" or "off" or "status"))
        {
            stderr.WriteLine("usage: ahd login on|off|status|run [--plain] [--dry-run]");
            return 2;
        }

        if (item is null)
        {
            if (action == "status")
            {
                Print(stdout, plain, "unsupported", false, false, [], [], null);
                return 0;
            }

            stderr.WriteLine("ahd: login is not supported on this platform");
            return 1;
        }

        return action switch
        {
            "status" => Status(item, stdout, plain),
            "on" => On(context, item, stdout, stderr, plain, dryRun),
            _ => Off(context, item, stdout, stderr, plain, dryRun),
        };
    }

    private static int Status(LoginItem item, TextWriter stdout, bool plain)
    {
        var state = LoginState.Describe(item, out var reason);
        Print(stdout, plain, state, false, false, [], [], reason);
        return 0;
    }

    private static int On(CliContext context, LoginItem item, TextWriter stdout, TextWriter stderr, bool plain, bool dryRun)
    {
        if (Refusal(context, item) is { } refusal)
        {
            stderr.WriteLine(refusal);
            return 2;
        }

        if (item.SessionProblem() is { } problem)
        {
            stderr.WriteLine(problem);
            return 1;
        }

        var wanted = item.Render(CliContext.RealPath(context.ProcessPath));
        var existing = ReadBytes(item.UnitPath);
        if (existing is not null && !item.OwnedBy(Encoding.UTF8.GetString(existing)))
        {
            stderr.WriteLine($"ahd: {item.UnitPath} exists and is not ahd's; refusing to replace it");
            return 1;
        }

        var same = existing is not null && existing.AsSpan().SequenceEqual(wanted);
        var loaded = item.Loaded();
        if (same && loaded)
        {
            Print(stdout, plain, "on", false, dryRun, [], [], "already on");
            return 0;
        }

        var commands = item.StartCommands(loaded, !same);
        var loginJson = Path.Combine(context.Env.AhHome, "login.json");
        var files = same ? new[] { loginJson } : [loginJson, item.UnitPath];
        if (dryRun)
        {
            Print(stdout, plain, "on", true, true, files, commands, null);
            return 0;
        }

        var stay = new JsonObject();
        stay["stay"] = true;
        stay["since"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        WriteAtomic(loginJson, JsonText.ToUtf8(stay, 0).Concat("\n"u8.ToArray()).ToArray());
        if (!same)
        {
            WriteAtomic(item.UnitPath, wanted);
        }

        foreach (var command in commands)
        {
            if ((context.Runner.Run(new(command)).ExitCode ?? 1) != 0)
            {
                stderr.WriteLine($"ahd: {string.Join(' ', command)} failed");
                return 1;
            }
        }

        Print(stdout, plain, "on", true, false, files, commands, null);
        return 0;
    }

    private static int Off(CliContext context, LoginItem item, TextWriter stdout, TextWriter stderr, bool plain, bool dryRun)
    {
        if (Refusal(context, item) is { } refusal)
        {
            stderr.WriteLine(refusal);
            return 2;
        }

        var existing = ReadBytes(item.UnitPath);
        if (existing is not null && !item.OwnedBy(Encoding.UTF8.GetString(existing)))
        {
            stderr.WriteLine($"ahd: {item.UnitPath} exists and is not ahd's; refusing to remove it");
            return 1;
        }

        var loginJson = Path.Combine(context.Env.AhHome, "login.json");
        var loaded = existing is not null || item.Loaded();
        if (existing is null && !loaded && !File.Exists(loginJson))
        {
            Print(stdout, plain, "off", false, dryRun, [], [], "already off");
            return 0;
        }

        var commands = loaded ? item.StopCommands() : [];
        var files = new List<string>();
        if (existing is not null)
        {
            files.Add(item.UnitPath);
        }

        if (File.Exists(loginJson))
        {
            files.Add(loginJson);
        }

        if (dryRun)
        {
            Print(stdout, plain, "off", true, true, files, commands, null);
            return 0;
        }

        foreach (var command in commands)
        {
            // A job that is not loaded is already stopped: the exit status is not an error here.
            context.Runner.Run(new(command));
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }

        Print(stdout, plain, "off", true, false, files, commands, null);
        return 0;
    }

    private static string? Refusal(CliContext context, LoginItem item) =>
        LoginGuard.Refusal(context, item) ?? LoginGuard.AhHomeRefusal(context);

    private static void Print(
        TextWriter stdout, bool plain, string state, bool changed, bool dryRun,
        IReadOnlyList<string> files, IReadOnlyList<string[]> commands, string? message)
    {
        var rendered = commands.Select(c => string.Join(' ', c)).ToList();
        if (plain)
        {
            var sb = new StringBuilder();
            sb.Append("login: ").Append(state).Append('\n');
            if (message is not null)
            {
                sb.Append(message).Append('\n');
            }

            foreach (var file in files)
            {
                sb.Append(dryRun ? "would write or remove " : changed ? "wrote or removed " : "unchanged ").Append(file).Append('\n');
            }

            foreach (var command in rendered)
            {
                sb.Append(dryRun ? "would run " : "ran ").Append(command).Append('\n');
            }

            stdout.Write(sb.ToString());
            return;
        }

        var json = new JsonObject();
        json["login"] = state;
        json["changed"] = changed;
        json["dry_run"] = dryRun;
        json["files"] = Strings(files);
        json["commands"] = Strings(rendered);
        json["message"] = message is null ? JsonValue.Null : message;
        stdout.Write(JsonText.Stringify(json, 0) + "\n");
    }

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.tmp.{Environment.ProcessId}";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
