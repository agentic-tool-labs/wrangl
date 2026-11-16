using System.Globalization;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Adds or removes one line of <c>trusted-homes</c> (or, with <c>--hierarchy</c>, <c>trusted-hierarchy-dirs</c>). It refuses unless
/// standard input and standard error are terminals, no agent session started the process and, outside test mode, no Claude Code
/// session marker is in its environment. Those are friction against an accident, not proof of who is typing; it writes a same-directory temporary
/// file with mode 0600 and renames it over the target, and it checks the result with the trust-store service that reads the file.
/// It never removes a file, and it rewrites a file only when the trust-store service currently honours it.
/// </summary>
internal static class HomeTrust
{
    private const string Usage = "usage: ahd home trust|untrust <path> [--hierarchy]";

    private const uint GroupOrOtherWritable = 0x12;

    internal static int Run(IHomeTrustServices services, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        var action = rest.Length > 0 ? rest[0] : string.Empty;
        if (action is not ("trust" or "untrust"))
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        if (services.Environment.GetVariable("AH_SPAWN_DEPTH") is not null)
        {
            stderr.WriteLine($"ahd home {action}: refused. This process was started by an agent session: run it from your own terminal.");
            return 2;
        }

        if (!services.Environment.TestMode
            && (services.Environment.GetVariable("CLAUDECODE") is not null || services.Environment.GetVariable("CLAUDE_CODE_SESSION_ID") is not null))
        {
            stderr.WriteLine($"ahd home {action}: refused. This command was started from a Claude Code session: run it from a terminal that Claude Code did not start.");
            return 2;
        }

        if (!services.IsTerminal())
        {
            stderr.WriteLine($"ahd home {action}: refused. Standard input and standard error must both be terminals: run it from your own terminal.");
            return 2;
        }

        var hierarchy = false;
        string? target = null;
        var literal = false;
        foreach (var token in rest.Skip(1))
        {
            if (!literal && token == "--")
            {
                literal = true;
            }
            else if (!literal && token == "--hierarchy")
            {
                hierarchy = true;
            }
            else if (!literal && token.StartsWith('-'))
            {
                stderr.WriteLine($"ahd home {action}: unrecognized flag {token}");
                stderr.WriteLine(Usage);
                return 2;
            }
            else if (target is null)
            {
                target = token;
            }
            else
            {
                stderr.WriteLine(Usage);
                return 2;
            }
        }

        if (string.IsNullOrEmpty(target))
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        if (services.CurrentUserId is not { } me)
        {
            stderr.WriteLine($"ahd home {action}: refused. The invoking user is not known to this system.");
            return 2;
        }

        var file = hierarchy ? TrustFile.HierarchyDirs : TrustFile.Homes;
        var before = services.Trust.Read(file);
        if (!Path.IsPathRooted(before.Path) || Path.GetDirectoryName(before.Path) is not { Length: > 0 } home)
        {
            stderr.WriteLine($"ahd home {action}: refused. The default home cannot be determined.");
            return 2;
        }

        try
        {
            return Apply(services, action, file, before, home, me, Path.GetFullPath(target), stdout, stderr);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"ahd home {action}: {e.Message}");
            return 1;
        }
    }

    private static int Apply(
        IHomeTrustServices services,
        string action,
        TrustFile file,
        TrustFileReport before,
        string home,
        uint me,
        string fullPath,
        TextWriter stdout,
        TextWriter stderr)
    {
        var files = services.Files;
        string? real = null;
        try
        {
            real = files.RealPath(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A path that no longer exists can still be removed by the text it was listed under.
        }

        if (action == "trust" && V1HomeRefusal(files, home, fullPath) is { } v1Refusal)
        {
            stderr.WriteLine($"ahd home trust: {v1Refusal}; nothing was written");
            return 1;
        }

        if (action == "trust")
        {
            var why = real is null ? $"{fullPath} does not exist" : DirectoryProblem(files, real, me);
            if (why is not null)
            {
                stderr.WriteLine($"ahd home trust: {why}; nothing was written");
                return 1;
            }
        }

        var homeExists = files.Stat(home) is not null;
        if (homeExists && HomeProblem(files, home, me) is { } homeProblem)
        {
            stderr.WriteLine($"ahd home {action}: {homeProblem}; nothing was written");
            return 1;
        }

        var text = string.Empty;
        if (before.Present)
        {
            if (before.IgnoredReason is not null)
            {
                stderr.WriteLine($"ahd home {action}: {before.Path} is currently ignored ({before.IgnoredReason}); fix or remove it yourself, nothing was written");
                return 1;
            }

            text = files.ReadText(before.Path);
        }

        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var changed = false;
        if (action == "trust")
        {
            var entry = real!;
            if (!lines.Any(l => l.Trim() == entry))
            {
                lines.Add(entry);
                changed = true;
            }
        }
        else
        {
            var named = lines.Where(l => l.Trim() == fullPath || (real is not null && l.Trim() == real)).ToList();
            if (named.Count == 0)
            {
                stderr.WriteLine($"ahd home untrust: {fullPath} is not listed in {before.Path}; nothing was written");
                return 1;
            }

            lines = lines.Where(l => !named.Contains(l)).ToList();
            changed = true;
        }

        if (changed)
        {
            var bytes = Encoding.UTF8.GetBytes(lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n");
            if (!homeExists)
            {
                services.Writer.Mkdir(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var temporary = before.Path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            services.Writer.WriteAtomic(before.Path, temporary, bytes, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var after = services.Trust.Read(file);
        var path = real ?? fullPath;
        var problem = Problem(action, after, path);
        var result = new JsonObject
        {
            ["ok"] = problem is null,
            ["action"] = action,
            ["file"] = before.Path,
            ["path"] = path,
            ["changed"] = changed,
        };
        if (problem is not null)
        {
            result["error"] = problem;
            stderr.WriteLine($"ahd home {action}: {problem}");
        }

        stdout.WriteLine(JsonText.Stringify(result, 0));
        return problem is null ? 0 : 1;
    }

    /// <summary>
    /// The refusal text when the target is the v1 plugin's default home or anything under it, by path segment on the real path (a
    /// path that does not exist yet is judged by its nearest existing ancestor); null when it is not. A v1 home that cannot be
    /// resolved refuses, with its own text.
    /// </summary>
    private static string? V1HomeRefusal(IFileReader files, string home, string fullPath)
    {
        const string Unresolved = "ah v1's home path (~/.claude/agent-hierarchy) cannot be resolved (a dangling link?)";
        var userHome = Path.GetDirectoryName(Path.GetDirectoryName(home));
        if (string.IsNullOrEmpty(userHome))
        {
            return Unresolved;
        }

        var v1 = Ah.Core.Text.RealPathResolver.ThroughLongestExistingPrefix(files, Ah.Core.Text.V1Layout.HomeUnder(userHome));
        if (v1 is null)
        {
            return Unresolved;
        }

        return Ah.Core.Text.RealPathResolver.ThroughLongestExistingPrefix(files, fullPath) is { } target
            && Ah.Core.Text.V1Layout.IsAtOrUnder(v1, target)
                ? "that is ah v1's home; Wrangl uses its own"
                : null;
    }

    /// <summary>Why <paramref name="real"/> would not count as a trust entry, or null when it would.</summary>
    private static string? DirectoryProblem(IFileReader files, string real, uint me)
    {
        if (real.Contains('\n', StringComparison.Ordinal) || real.Contains('\r', StringComparison.Ordinal) || real != real.Trim())
        {
            return $"{real} has a name a one-path-per-line file cannot hold";
        }

        return files.Stat(real) is not { Kind: FileKind.Directory } d
            ? $"{real} is not a directory"
            : d.Uid != me
                ? $"{real} is not owned by you"
                : (d.Mode & GroupOrOtherWritable) != 0 ? $"{real} is writable by its group or others" : null;
    }

    private static string? HomeProblem(IFileReader files, string home, uint me) =>
        files.Stat(home) is not { Kind: FileKind.Directory } d
            ? $"{home} is not a directory"
            : d.Uid != me
                ? $"{home} is not owned by you"
                : (d.Mode & GroupOrOtherWritable) != 0 ? $"{home} is writable by its group or others" : null;

    /// <summary>What is wrong with the file as the trust-store service now reads it, or null when it holds what the verb meant to leave.</summary>
    private static string? Problem(string action, TrustFileReport after, string path)
    {
        if (after.IgnoredReason is not null)
        {
            return $"{after.Path} is written but ignored: {after.IgnoredReason}";
        }

        var entry = after.Entries.FirstOrDefault(e => e.Line.Trim() == path || e.RealPath == path);
        return action == "trust"
            ? entry is null ? $"{path} is not in {after.Path} after the write"
                : entry.Trusted ? null : $"{path} is listed but ignored: {entry.Reason}"
            : entry is null ? null : $"{path} is still listed in {after.Path}";
    }
}
