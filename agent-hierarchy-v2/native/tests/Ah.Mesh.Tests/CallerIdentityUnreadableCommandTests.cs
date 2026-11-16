using Ah.Abstractions;

namespace Ah.Mesh.Tests;

/// <summary>The user verdict needs the caller's whole line, and every live registration's start and terminal, read: what cannot be read is never the user.</summary>
public sealed class CallerIdentityUnreadableCommandTests
{
    private const string Start = "Thu Nov  5 10:00:00 2026";

    /// <summary>
    /// Caller 300 under 200 (the harness slot) under 100 under pid 1, at a terminal; a registered session runs as 400. A command is
    /// "-" for unreadable, and "a|b" answers a on the first read and b after.
    /// </summary>
    private sealed class Line(string c300, string c200, string c100, string? liveTty) : IProcessTable
    {
        private readonly Dictionary<int, int> _reads = [];

        public IReadOnlyList<int>? Ancestors(int pid) => pid == 300 ? [200, 100, 1] : null;

        public bool? HasAncestor(int pid, int candidate) => pid == 300 && candidate is 200 or 100 or 1;

        public string? StartTime(long pid) => pid == 400 ? Start : null;

        public string? LocalStartTime(long pid) => null;

        public string? Tty(int pid) => pid switch { 300 => "ttys001", 400 => liveTty, _ => null };

        public string? Command(int pid)
        {
            var spec = pid switch { 300 => c300, 200 => c200, 100 => c100, _ => "-" };
            var n = _reads[pid] = _reads.GetValueOrDefault(pid) + 1;
            var answers = spec.Split('|');
            var answer = answers[Math.Min(n, answers.Length) - 1];
            return answer == "-" ? null : answer;
        }
    }

    [Theory]
    [InlineData("zsh", "-", "zsh", PrincipalKind.Anonymous)] // the harness pid's command could not be read
    [InlineData("zsh", "claude|-", "zsh", PrincipalKind.Anonymous)] // read once: "claude", then null on a second read, is still the harness
    [InlineData("zsh", "zsh", "-", PrincipalKind.Anonymous)] // an unreadable non-harness ancestor
    [InlineData("-", "zsh", "zsh", PrincipalKind.Anonymous)] // the caller's own command unreadable
    [InlineData("zsh", "zsh", "zsh", PrincipalKind.User)] // negative: a readable line with no harness still judges as the user
    public void AnUnreadableCommandOnTheLineIsNeverTheUser(string c300, string c200, string c100, PrincipalKind expected)
    {
        var principal = CallerIdentity.Resolve(new Line(c300, c200, c100, null), 300, [], (_, _) => false);

        Assert.Equal(expected, principal.Kind);
    }

    [Theory]
    [InlineData(null, Start, PrincipalKind.Anonymous)] // a live registration whose terminal cannot be read
    [InlineData("", Start, PrincipalKind.User)] // a live registration with no terminal (headless) is not the caller's terminal
    [InlineData("ttys009", null, PrincipalKind.Anonymous)] // a registration whose start cannot be compared
    [InlineData("ttys009", "Wed Nov  4 09:00:00 2026", PrincipalKind.User)] // negative: a reused pid is not that registration
    [InlineData("ttys009", Start, PrincipalKind.User)] // negative: a live registration on another terminal
    public void ALiveRegistrationThatCannotBeReadIsNeverTheUser(string? liveTty, string? recordedStart, PrincipalKind expected)
    {
        var live = new[] { new SessionRegistration("s-other", 400, recordedStart, ["imp@tm"]) };

        var principal = CallerIdentity.Resolve(new Line("zsh", "zsh", "zsh", liveTty), 300, live, (_, _) => false);

        Assert.Equal(expected, principal.Kind);
    }
}
