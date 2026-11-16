using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core;
using Ah.Core.Shell;
using Ah.Core.Text;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;
using Ah.Hooks.Surface;
using Ah.Roster;

namespace Ah.Hooks.Gates;

/// <summary>
/// Fewer-turns S6, allow-only (ah-simplify S1). In a non-self-care member session it allows a compound whose every segment is an ah call
/// or a read-only command whose every path operand resolves inside the member's work tree (so Claude Code's classifier is skipped). In
/// every other case it is silent; it never denies.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class CompoundAllowGate
{
    /// <summary>The gate never denies, so nothing it emits stays hard.</summary>
    internal static bool StaysHard(GateContext c) => false;

    private const int MaxCommand = 4096;

    private static readonly string[] RedirectTokens = ["2>&1", ">/dev/null", "2>/dev/null"];
    private static readonly string[] AllowedAssignments = ["NO_COLOR", "LC_ALL", "LANG", "TZ"];

    /// <summary>
    /// The programs the allow judges (S6.3 table). Every row is in <c>BuildingBlocks</c> or <c>SandboxedBlocks</c>; a test holds that.
    /// Flag lists are the spec table's (2026-10-29), not re-derived from each tool's help. Versions present on the build host that day: git 2.54.0
    /// (Apple), ripgrep 15.1.0, BSD grep 2.6.0-FreeBSD, Apple sort 2.3 and Apple diff; re-audit the lists against each tool's current --help when a block's version changes.
    /// BSD spellings (review F2): grep -S and -O follow symlinks like -R, so all three are silent; any recursive grep (-r) is silent too (F5), because it reads
    /// .claude, .git, .envrc and settings files without naming them. rg stays allowed only without --hidden, -., -u, --no-ignore*, -L and --follow. Residual: rg skips
    /// hidden files by default, which covers .claude, .git, .envrc and .mcp.json, but a non-hidden settings*.json inside the tree is readable. sort --files0-from is silent (F3).
    /// </summary>
    internal static readonly string[] TableRows =
    [
        "cd", "pwd", "true", "echo", "printf", "basename", "dirname", "test",
        "ls", "cat", "head", "tail", "wc", "cut", "tr", "grep", "rg", "sed", "sort", "uniq", "diff", "date",
        "git ls-files", "git grep", "git blame", "git diff", "git log", "git show",
    ];

    [GeneratedRegex(@"(?<=^|\s)(2>&1|>/dev/null|2>/dev/null)(?=$|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex RedirectToken();

    [GeneratedRegex(@"^\d+(,\d+)?p$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPrint();

    public static HookOutcome Run(GateContext c)
    {
        if (c.ToolName() != "Bash" || c.Command() is not { Length: > 0 } command || command.Length > MaxCommand)
        {
            return HookEmit.Silent;
        }

        // S6.2.2
        var s = c.Services;
        var hit = s.Config.ResolveHierarchyRole(c.Input);
        if (hit.Role is null || hit.Role == "orchestrator")
        {
            return HookEmit.Silent;
        }

        // S6.2.3
        var session = SessionOf(c);
        if (session is null)
        {
            return HookEmit.Silent;
        }

        var segments = CoreModule.ShellCommandsChecked(command, out var unreadable);
        if (unreadable || AhCliGate.TokenizerGaveUp(command))
        {
            return HookEmit.Silent;
        }

        return new Judge(c, session, command, segments, hit).Allow() ?? HookEmit.Silent;
    }

    /// <summary>The member session: its launch file reads and its work tree root is known.</summary>
    private sealed record Session(string WorkRoot, SessionSettings Settings, string Home);

    private static Session? SessionOf(GateContext c)
    {
        var s = c.Services;
        if (s.Advisory is not { } advisory || c.Input.Text("session_id") is not { Length: > 0 } sessionId
            || PermissionDeniedAlarm.MemberAddress(advisory, sessionId) is not { } address)
        {
            return null;
        }

        var dir = s.Config.HierarchyDir(c.Cwd());
        var teamHome = s.Roster.TeamHome(dir);
        var root = s.Roster.ReadTeamRecord(dir, address.Team).Member("members") is { IsArray: true } members
            ? members.AsArray().Where(m => m.Member("name") is { IsText: true } n && n.AsString() == address.Name)
                .Select(static m => m.Member("expected_root") is { IsText: true } r ? r.AsString() : null).FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(teamHome) || string.IsNullOrEmpty(root) || s.Environment.HomeDirectory is not { Length: > 0 } home)
        {
            return null;
        }

        var settings = RosterModule.ReadSessionSettings(s.Files, home, teamHome, address.Name, root);
        return settings.Scopes[0] is { Exists: true, Error: null } ? new Session(root, settings, home) : null;
    }

    /// <summary>An ah or ahd call, which a compound may hold (S1: allowed, no longer denied).</summary>
    private static bool IsAh(CommandSegment g)
    {
        if (g.Program >= g.Words.Count)
        {
            return false;
        }

        var program = g.Words[g.Program];
        if (AhCli.IsAhBinary(program) || g.ProgramName is "ah" or "ahd")
        {
            return true;
        }

        return g.ProgramName == "node" && g.Words.Skip(g.Program + 1).Any(w => Regex.IsMatch(w, @"(^|/)hooks/(roster|msg|ahd-ctl)\.mjs$", RegexOptions.CultureInvariant));
    }

    // ---- S6.2.4-6 and S6.3: the allow ------------------------------------------------------------------------------------------

    private sealed class Judge
    {
        private readonly GateContext c;
        private readonly Session session;
        private readonly string command;
        private readonly IReadOnlyList<CommandSegment> segments;
        private readonly HierarchyRole role;
        private readonly IFileReader files;
        private string root = string.Empty;
        private readonly List<string> rwRules = [];
        private bool everything;
        private bool uninterpreted;

        public Judge(GateContext c, Session session, string command, IReadOnlyList<CommandSegment> segments, HierarchyRole role)
        {
            this.c = c;
            this.session = session;
            this.command = command;
            this.segments = segments;
            this.role = role;
            files = c.Services.Files;
        }

        public HookOutcome? Allow()
        {
            // S6.2.4
            var settings = session.Settings;
            if (settings.ManagedPresent || settings.Scopes.Any(g => g.Error is not null))
            {
                return null;
            }

            // S6.2.5 (S6.8 G5)
            var config = c.Services.Config;
            if (config.IsSubagent(c.Input) && !role.Direct)
            {
                return null;
            }

            // S6.2.6
            if (CanonicalPath.Resolve(files, session.WorkRoot) is not { } realRoot || CanonicalPath.Resolve(files, c.Cwd()) is not { } cwd
                || !CanonicalPath.IsUnder(cwd, realRoot))
            {
                return null;
            }

            root = realRoot;

            // Raw-text rules 2-4 run before any parsing.
            if (RawTextRefused(out var deletedRedirects))
            {
                return null;
            }

            // Rule 1
            if (segments.Count <= 1 && deletedRedirects == 0)
            {
                return null;
            }

            if (segments.Count == 0 || !SortRules())
            {
                return null;
            }

            var gaps = Gaps();
            if (gaps is null)
            {
                return null;
            }

            var programs = new List<string>();
            var cur = cwd;
            for (var k = 0; k < segments.Count; k++)
            {
                var g = segments[k];
                var ah = !g.Nested && g.Wrappers.Count == 0 && IsAh(g);
                if (!ah && (!PlainSegment(g) || JudgeSegment(g, k, gaps, ref cur) is null))
                {
                    return null;
                }

                var name = ah ? "ah" : g.ProgramName;
                if (!programs.Contains(name))
                {
                    programs.Add(name);
                }
            }

            return HookEmit.Decision("allow", $"ah: read-only compound ({string.Join(", ", programs)})");
        }

        // Rules 2-4 ------------------------------------------------------------------------------------------------------------

        private bool RawTextRefused(out int deleted)
        {
            deleted = 0;
            for (var i = 0; i < command.Length; i++)
            {
                var ch = command[i];
                if ((ch == '$' && (i + 1 >= command.Length || command[i + 1] != '?')) || ch is '`' or '(' or ')' or '#' or '<'
                    || (ch == '\\' && i + 1 < command.Length && command[i + 1] == '\n')
                    || (char.IsControl(ch) && ch is not ('\n' or '\t')))
                {
                    return true;
                }
            }

            var count = 0;
            var rest = RedirectToken().Replace(command, _ =>
            {
                count++;
                return " ";
            });
            deleted = count;
            if (rest.Contains('>', StringComparison.Ordinal))
            {
                return true;
            }

            return rest.Replace("&&", " ", StringComparison.Ordinal).Contains('&', StringComparison.Ordinal);
        }

        // Rule 9 inputs ----------------------------------------------------------------------------------------------------------

        /// <summary>Sorts the Read/Edit/Write rules of every scope; false when a Bash rule matches a segment (deny, ask, excludedCommands).</summary>
        private bool SortRules()
        {
            foreach (var scope in session.Settings.Scopes)
            {
                foreach (var rule in scope.Deny.Concat(scope.Ask))
                {
                    if (rule is "Read" or "Edit" or "Write")
                    {
                        everything = true;
                    }
                    else if (ToolRuleBody(rule) is { } body)
                    {
                        if (body is "" or "*" or "**")
                        {
                            everything = true;
                        }
                        else
                        {
                            rwRules.Add(body);
                            uninterpreted |= body.AsSpan().IndexOfAny("?[{\\") >= 0;
                        }
                    }
                }
            }

            if (everything)
            {
                return false;
            }

            var bashRules = session.Settings.Scopes.SelectMany(g => g.Deny.Concat(g.Ask)).Where(r => r == "Bash" || r.StartsWith("Bash(", StringComparison.Ordinal) && r.EndsWith(')'))
                .Select(r => r == "Bash" ? string.Empty : r[5..^1])
                .Concat(session.Settings.Scopes.SelectMany(g => g.ExcludedCommands));
            foreach (var body in bashRules)
            {
                var words = RuleWords(body);
                foreach (var g in segments)
                {
                    var sw = g.Words.Skip(g.Program).ToList();
                    if (words.Count <= sw.Count && words.Select((w, i) => SurfaceMatcher.GlobMatch(w, 0, sw[i], 0) || MemberPermissionsGenerator.GlobMatches(w, sw[i])).All(m => m))
                    {
                        return false;
                    }
                }
            }

            // S6.9: every rule, of any shape, is also matched whole (`*` matches anything, anchored) against this command's candidates.
            var candidates = Candidates();
            foreach (var body in bashRules)
            {
                var pattern = body.EndsWith(":*", StringComparison.Ordinal) ? body[..^2] + "*" : body;
                if (candidates.Any(t => MemberPermissionsGenerator.GlobMatches(pattern, t)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// S6.9 candidates, per top-level segment: its raw text, the text from its start to the end, and the tokenizer's de-quoted words
        /// (all of them, and from the program word on, assignments stripped) alone and joined into tails with the raw separators kept.
        /// Each appears with its redirect tokens replaced by a space and again deleted and trimmed.
        /// </summary>
        private List<string> Candidates()
        {
            var bases = new List<string>();
            var words = string.Empty;
            var program = string.Empty;
            var rawProgram = string.Empty;
            for (var k = segments.Count - 1; k >= 0; k--)
            {
                var g = segments[k];
                var next = k + 1 < segments.Count ? segments[k + 1].Start : command.Length;
                if (g.Start < 0 || g.End < g.Start || next < g.End || next > command.Length)
                {
                    break; // Gaps() makes the gate silent on such segments.
                }

                // The raw text from the program word on: the segment's leading assignments stripped by the tokenizer's own word offset.
                var ownProgram = g.ProgramStart >= g.Start && g.ProgramStart <= g.End ? command[g.ProgramStart..g.End] : command[g.Start..g.End];
                rawProgram = ownProgram + command[g.End..next] + rawProgram;
                bases.AddRange([ownProgram, rawProgram]);

                var all = string.Join(' ', g.Words);
                var fromProgram = string.Join(' ', g.Words.Skip(g.Program));
                words = all + command[g.End..next] + words;
                program = fromProgram + command[g.End..next] + program;
                bases.AddRange([command[g.Start..g.End], command[g.Start..], all, fromProgram, words, program]);
            }

            return [.. bases.Select(t => RedirectToken().Replace(t, " ")), .. bases.Select(t => RedirectToken().Replace(t, string.Empty).Trim())];
        }

        private static string? ToolRuleBody(string rule)
        {
            foreach (var tool in new[] { "Read(", "Edit(", "Write(" })
            {
                if (rule.StartsWith(tool, StringComparison.Ordinal) && rule.EndsWith(')'))
                {
                    return rule[tool.Length..^1];
                }
            }

            return null;
        }

        private static string StripWildcardTail(string body) =>
            body.EndsWith(":*", StringComparison.Ordinal) || body.EndsWith(" *", StringComparison.Ordinal) ? body[..^2] : body;

        private static List<string> RuleWords(string body)
        {
            var text = StripWildcardTail(body);
            return [.. text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)];
        }

        /// <summary>The absolute globs a Read/Edit/Write rule body stands for, given the cwd in effect (S6.8 G1.4).</summary>
        private IEnumerable<string> MappedGlobs(string cur)
        {
            foreach (var p in rwRules)
            {
                if (p.StartsWith("//", StringComparison.Ordinal))
                {
                    yield return p[1..];
                }
                else if (p.Length > 1 && p[0] == '~' && p[1] == '/')
                {
                    yield return session.Home.TrimEnd('/') + p[1..];
                }
                else if (p.StartsWith('/'))
                {
                    yield return root + p;
                    yield return p;
                }
                else if (p.StartsWith("./", StringComparison.Ordinal))
                {
                    yield return cur + p[1..];
                }
                else if (p.Contains('/', StringComparison.Ordinal))
                {
                    yield return cur + "/" + p;
                }
                else
                {
                    yield return "**/" + p;
                }
            }
        }

        private bool RwHit(string real, string cur)
        {
            foreach (var glob in MappedGlobs(cur))
            {
                for (var p = real; p.Length > 0; p = p[..Math.Max(0, p.LastIndexOf('/'))])
                {
                    if (SurfaceMatcher.GlobMatch(glob, 0, p, 0))
                    {
                        return true;
                    }
                }

                if (SurfaceMatcher.GlobMatch(glob, 0, "/", 0))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TreeWideBlocked(string cur)
        {
            foreach (var glob in MappedGlobs(cur))
            {
                if (glob.StartsWith("**", StringComparison.Ordinal))
                {
                    return true;
                }

                var first = glob.AsSpan().IndexOfAny("*?[{");
                var literal = first < 0 ? glob : glob[..first];
                if (!literal.StartsWith('/'))
                {
                    return true;
                }

                var dir = literal.EndsWith('/') ? literal.TrimEnd('/') : literal[..Math.Max(0, literal.LastIndexOf('/'))];
                if (dir.Length == 0 || CanonicalPath.IsUnder(dir, root) || CanonicalPath.IsUnder(root, dir))
                {
                    return true;
                }
            }

            return false;
        }

        // Separators (S6.8 G2) -----------------------------------------------------------------------------------------------------

        private enum Sep { And, Seq, Pipe, Or }

        /// <summary>The separator before each segment (index 0 unused); null when the text between two segments is anything else.</summary>
        private Sep[]? Gaps()
        {
            var result = new Sep[segments.Count];
            var prev = 0;
            for (var k = 0; k < segments.Count; k++)
            {
                var g = segments[k];
                if (g.Start < prev || g.End < g.Start || g.End > command.Length)
                {
                    return null;
                }

                var gap = StripTokens(command[prev..g.Start]);
                if (k == 0)
                {
                    if (gap.Length > 0)
                    {
                        return null;
                    }
                }
                else if (Classify(gap) is { } sep)
                {
                    result[k] = sep;
                }
                else
                {
                    return null;
                }

                prev = g.End;
            }

            var tail = StripTokens(command[prev..]);
            return tail.Length == 0 || tail.All(ch => ch is ';' or '\n') ? result : null;
        }

        private static string StripTokens(string text) =>
            RedirectToken().Replace(text, " ").Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal);

        private static Sep? Classify(string gap) => gap switch
        {
            "&&" => Sep.And,
            "||" => Sep.Or,
            "|" => Sep.Pipe,
            { Length: > 0 } when gap.All(ch => ch is ';' or '\n') => Sep.Seq,
            _ => null,
        };

        // Rule 6 and the table -------------------------------------------------------------------------------------------------------

        private static bool PlainSegment(CommandSegment g) =>
            !g.Nested && g.Wrappers.Count == 0 && g.Reliable && !g.Derived && g.Program < g.Words.Count && !g.Words[g.Program].Contains('/', StringComparison.Ordinal)
            && g.Assigned.All(a => AllowedAssignments.Contains(a));

        /// <summary>Judges one segment; the table key when it may run, else null.</summary>
        private string? JudgeSegment(CommandSegment g, int k, Sep[] gaps, ref string cur)
        {
            var program = g.ProgramName;
            var args = g.Words.Skip(g.Program + 1).Where(w => !RedirectTokens.Contains(w)).ToList();
            string key = program;
            if (program == "git")
            {
                if (args.Count == 0 || args[0] is not ("ls-files" or "grep" or "blame" or "diff" or "log" or "show"))
                {
                    return null;
                }

                key = "git " + args[0];
                args.RemoveAt(0);
            }

            if (!TableRows.Contains(key))
            {
                return null;
            }

            if (key == "cd")
            {
                return JudgeCd(args, k, gaps, ref cur) ? key : null;
            }

            var here = cur;
            var scan = new Scan();
            var ok = key switch
            {
                "pwd" or "true" or "echo" or "printf" or "basename" or "dirname" or "tr" => true,
                "test" => ScanTest(args, scan),
                "date" => ScanOptions(args, scan, "sf", ["set", "file"]) && scan.Operands.Count == 0 && scan.AfterDashDash.Count == 0,
                "ls" or "cat" or "head" or "tail" or "wc" or "cut" => ScanOptions(args, scan, string.Empty, []),
                "sort" => ScanOptions(args, scan, "oT", ["output", "compress-program", "temporary-directory", "files0-from"]),
                "uniq" => ScanOptions(args, scan, string.Empty, []) && scan.Operands.Count + scan.AfterDashDash.Count <= 1,
                "diff" => ScanOptions(args, scan, "r", ["recursive"]),
                "sed" => ScanSed(args, scan),
                "grep" => ScanOptions(args, scan, "efROS", ["regexp", "file", "dereference-recursive"]) && !scan.TreeWide && DropPattern(scan),
                "rg" => ScanRg(args, scan),
                "git ls-files" or "git blame" => ScanOptions(args, scan, "Of", ["open-files-in-pager", "textconv", "ext-diff"]),
                "git grep" => ScanOptions(args, scan, "Of", ["open-files-in-pager", "textconv", "ext-diff", "untracked", "no-index", "no-exclude-standard"]),
                _ => ScanOptions(args, scan, "O", ["output", "ext-diff", "textconv", "no-index"]),
            };
            if (!ok)
            {
                return null;
            }

            var treeWide = key is "rg" or "git grep" or "git log" or "git show" or "git diff" || (key == "grep" && scan.TreeWide);
            if (treeWide && TreeWideBlocked(cur))
            {
                return null;
            }

            var paths = new List<string>(scan.Values);
            if (key is "git diff" or "git log" or "git show")
            {
                paths.AddRange(scan.Operands.Where(w => w.StartsWith('/') || w.StartsWith('~') || w.Contains("..", StringComparison.Ordinal) || w.AsSpan().IndexOfAny("*?[{") >= 0 || Exists(w, here)));
                paths.AddRange(scan.AfterDashDash);
            }
            else
            {
                paths.AddRange(scan.Operands);
                paths.AddRange(scan.AfterDashDash);
            }

            return paths.All(p => PathOk(p, here)) ? key : null;
        }

        private bool Exists(string word, string cur)
        {
            var abs = word.StartsWith('/') ? word : cur + "/" + word;
            return CanonicalPath.Resolve(files, abs) is not { } real || files.Exists(real);
        }

        private bool JudgeCd(List<string> args, int k, Sep[] gaps, ref string cur)
        {
            if (args.Count != 1 || !(args[0].StartsWith('/') || args[0].StartsWith("./", StringComparison.Ordinal) || args[0].StartsWith("../", StringComparison.Ordinal)))
            {
                return false;
            }

            // A cd that fails leaves the old cwd, so only `&&` may follow it (a `;` would run the rest in the wrong directory).
            // G1: every later separator must be `&&`, so a failed cd stops the rest (a later `;`, `||` or `|` would run in the old cwd).
            if (k > 0 && gaps[k] is not (Sep.And or Sep.Seq))
            {
                return false;
            }

            for (var j = k + 1; j < segments.Count; j++)
            {
                if (gaps[j] is not Sep.And)
                {
                    return false;
                }
            }

            if (!PathOk(args[0], cur))
            {
                return false;
            }

            var abs = args[0].StartsWith('/') ? args[0] : cur + "/" + args[0];
            if (CanonicalPath.Resolve(files, abs) is not { } next || files.Stat(next) is not { Kind: FileKind.Directory } || !CanonicalPath.IsUnder(next, root))
            {
                return false;
            }

            cur = next;
            return true;
        }

        private sealed class Scan
        {
            public List<string> Operands { get; } = [];

            public List<string> AfterDashDash { get; } = [];

            public List<string> Values { get; } = [];

            public bool TreeWide { get; set; }
        }

        private static bool IsOption(string w) => w.Length > 1 && w[0] == '-' && w != "--";

        /// <summary>Splits the arguments into options and operands; false when a listed flag appears in any spelling (abbreviated long options included).</summary>
        private static bool ScanOptions(List<string> args, Scan scan, string banShort, string[] banLong, string[]? valueLong = null, string valueShort = "")
        {
            var afterDash = false;
            for (var i = 0; i < args.Count; i++)
            {
                var w = args[i];
                if (afterDash)
                {
                    scan.AfterDashDash.Add(w);
                }
                else if (w == "--")
                {
                    afterDash = true;
                }
                else if (!IsOption(w))
                {
                    scan.Operands.Add(w);
                }
                else if (w.StartsWith("--", StringComparison.Ordinal))
                {
                    var eq = w.IndexOf('=', StringComparison.Ordinal);
                    var name = eq < 0 ? w[2..] : w[2..eq];
                    if (name.Length == 0 || banLong.Any(b => b.StartsWith(name, StringComparison.Ordinal)))
                    {
                        return false;
                    }

                    if (name is "recursive" or "directories" || "recursive".StartsWith(name, StringComparison.Ordinal) || "directories".StartsWith(name, StringComparison.Ordinal))
                    {
                        scan.TreeWide = true;
                    }

                    if (eq >= 0)
                    {
                        scan.Values.Add(w[(eq + 1)..]);
                    }
                    else if (valueLong is not null && valueLong.Any(v => v.StartsWith(name, StringComparison.Ordinal)) && i + 1 < args.Count)
                    {
                        scan.Values.Add(args[++i]);
                    }
                }
                else
                {
                    var bundle = w[1..];
                    if (bundle.Any(ch => banShort.Contains(ch)))
                    {
                        return false;
                    }

                    if (bundle.Contains('r') || bundle.Contains('d'))
                    {
                        scan.TreeWide = true;
                    }

                    if (valueShort.Length > 0 && bundle.IndexOfAny(valueShort.ToCharArray()) is var at and >= 0)
                    {
                        if (at + 1 < bundle.Length)
                        {
                            scan.Values.Add(bundle[(at + 1)..]);
                        }
                        else if (i + 1 < args.Count)
                        {
                            scan.Values.Add(args[++i]);
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>The first operand of a plain <c>grep [flags] PATTERN [PATHS…]</c> is the pattern; it is not a path.</summary>
        private static bool DropPattern(Scan scan)
        {
            if (scan.Operands.Count > 0)
            {
                scan.Operands.RemoveAt(0);
            }
            else if (scan.AfterDashDash.Count > 0)
            {
                scan.AfterDashDash.RemoveAt(0);
            }

            return true;
        }

        private static bool ScanRg(List<string> args, Scan scan)
        {
            if (!ScanOptions(
                    args,
                    scan,
                    "efLzu.g",
                    [
                        "glob", "iglob", "regexp", "file", "follow", "pre", "pre-glob", "hostname-bin", "search-zip", "files", "type-list",
                        "hidden", "unrestricted", "no-ignore", "no-ignore-vcs", "no-ignore-dot", "no-ignore-global", "no-ignore-parent", "no-ignore-files",
                        "no-ignore-exclude", "no-ignore-messages",
                    ],
                    ["ignore-file"]))
            {
                return false;
            }

            scan.TreeWide = true;
            return DropPattern(scan);
        }

        private static bool ScanTest(List<string> args, Scan scan)
        {
            foreach (var w in args)
            {
                if (!IsOption(w))
                {
                    scan.Operands.Add(w);
                }
            }

            return true;
        }

        /// <summary>Only the numeric print form: <c>sed [-n|-E|-r…] 'A,Bp' files</c> (S6 D15).</summary>
        private static bool ScanSed(List<string> args, Scan scan)
        {
            var i = 0;
            while (i < args.Count && IsOption(args[i]))
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal) || args[i][1..].Any(ch => ch is not ('n' or 'E' or 'r')))
                {
                    return false;
                }

                i++;
            }

            if (i >= args.Count || !NumericPrint().IsMatch(args[i]))
            {
                return false;
            }

            scan.Operands.AddRange(args.Skip(i + 1));
            return !scan.Operands.Any(IsOption);
        }

        // Rule 7 -----------------------------------------------------------------------------------------------------------------------

        private static readonly string[] ProtectedSuffixes = [".git", ".claude", ".mcp.json", ".envrc", "CLAUDE.md", ".json"];

        private bool PathOk(string word, string cur)
        {
            if (word.Length == 0)
            {
                return true;
            }

            if (word[0] == '~' || word.Contains('{', StringComparison.Ordinal) || uninterpreted)
            {
                return false;
            }

            var glob = word.AsSpan().IndexOfAny("*?[");
            if (glob < 0)
            {
                var abs = word.StartsWith('/') ? word : cur + "/" + word;
                return CanonicalPath.Resolve(files, abs) is { } real && !IsProtected(real) && !RwHit(real, cur);
            }

            if (word.StartsWith('/') || word[..glob].Split('/').Contains("..") || rwRules.Count > 0)
            {
                return false;
            }

            var slash = word.LastIndexOf('/');
            if (slash > glob)
            {
                return false;
            }

            var final = word[(slash + 1)..];
            var suffix = final[(final.AsSpan().LastIndexOfAny("*?[]") + 1)..];
            if (suffix.Length == 0 || ProtectedSuffixes.Any(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || suffix.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return slash < 0 ? PathOk(".", cur) : PathOk(word[..slash].Length == 0 ? "/" : word[..slash], cur);
        }

        /// <summary>Whether a real path is protected (S6.3 rule 7), judged on the part below the work tree root; outside the tree is protected.</summary>
        internal bool IsProtected(string real)
        {
            if (!CanonicalPath.IsUnder(real, root))
            {
                return true;
            }

            var rel = real.Length > root.Length ? real[(root.Length + 1)..] : string.Empty;
            return ProtectedRelative(rel);
        }

        internal static bool ProtectedRelative(string rel)
        {
            var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals(".git",StringComparison.OrdinalIgnoreCase) || parts[i].Equals(".claude", StringComparison.OrdinalIgnoreCase)
                    || (parts[i].Equals(".github", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length && parts[i + 1].Equals("workflows", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return parts.Length > 0 && (parts[^1].Equals(".mcp.json", StringComparison.OrdinalIgnoreCase) || parts[^1].Equals(".envrc", StringComparison.OrdinalIgnoreCase)
                || parts[^1].Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
                || (parts[^1].StartsWith("settings", StringComparison.OrdinalIgnoreCase) && parts[^1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
