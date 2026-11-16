using Ah.Cli.Delegation;

namespace Ah.Cli.Login;

internal static class LoginGuard
{
    /// <summary>
    /// The message to refuse with (exit 2, nothing written), or null. $HOME must equal the password database's
    /// home for this user. The one exception is test mode: AH_TEST=1 and the service manager's tool resolving
    /// into a directory listed in AH_TEST_FAKE_BIN.
    /// </summary>
    internal static string? Refusal(CliContext context, LoginItem item)
    {
        var home = context.Env.HomeDirectory;
        if (context.PasswdHome is { } passwd && Canonical(home) == Canonical(passwd))
        {
            return null;
        }

        if (context.Env.TestMode && ToolIsFake(context, item.Tool))
        {
            return null;
        }

        return $"ahd: refusing to change the login item: $HOME ({home}) is not this user's passwd home ({context.PasswdHome ?? "unknown"})";
    }

    /// <summary>The default state home: <c>~/.claude/ah2</c>. Only that one is served by a login item.</summary>
    internal static string? AhHomeRefusal(CliContext context)
    {
        var defaultHome = Ah.Core.Text.AhPaths.HomeUnder(context.Env.HomeDirectory);
        return Canonical(context.Env.AhHome) == Canonical(defaultHome)
            ? null
            : $"ahd: a login item serves the default AH_HOME only ({defaultHome}); AH_HOME is {context.Env.AhHome}";
    }

    private static bool ToolIsFake(CliContext context, string tool)
    {
        var resolved = PathLookup.OnPath(tool, context.Env.GetVariable("PATH"));
        if (resolved is null)
        {
            return false;
        }

        var directory = Canonical(Path.GetDirectoryName(resolved)!);
        return (context.Env.GetTestOnly("AH_TEST_FAKE_BIN") ?? string.Empty).Split(':')
            .Where(d => d.Length > 0 && Directory.Exists(d))
            .Any(d => Canonical(d) == directory);
    }

    /// <summary>An absolute path with every symbolic link in it resolved.</summary>
    internal static string Canonical(string path)
    {
        var parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "/";
        foreach (var part in parts)
        {
            var next = Path.Combine(current, part);
            try
            {
                var target = new DirectoryInfo(next).LinkTarget;
                if (target is not null)
                {
                    next = Path.GetFullPath(Path.Combine(current, target));
                    next = Canonical(next);
                }
            }
            catch (IOException)
            {
                // not a link we can read: keep the component as it is
            }

            current = next;
        }

        return current;
    }
}
