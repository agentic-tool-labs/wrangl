using Ah.Abstractions;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Text;

namespace Ah.Cli.Composition;

/// <summary>What <c>roster doctor</c> reads beyond the libraries: the directive's status text, and git's view of a checkout.</summary>
internal sealed class DoctorComposition(ServiceGraph graph, IAhEnvironment environment) : IRosterDoctor
{
    private readonly IFileReader _files = graph.Io.Files;

    private IDirectiveText? _directive;

    public string StatusReport(string cwd)
    {
        _directive ??= ConfigModule.CreateDirectiveText(
            graph.Config,
            ConfigModule.CreateRoleContracts(graph.Config, _files, environment),
            () => graph.Roster);
        return _directive.StatusReport(cwd);
    }

    public IReadOnlyList<TrustFileReport> TrustFiles() =>
        [graph.Trust.Read(TrustFile.Homes), graph.Trust.Read(TrustFile.HierarchyDirs)];

    public IReadOnlyList<string> LiveRegistrationAddresses() =>
        graph.Mailbox.LiveRegistrations()
            .SelectMany(r => graph.Mailbox.RegistrationBoxes(r).Select(b => b.Address))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<string> HeldRelays()
    {
        var now = graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
        return
        [
            .. graph.Mailbox.ListBoxes()
                .SelectMany(b => graph.Mailbox.HeldRelays(b.Dir, now, TimeSpan.FromMinutes(10).TotalMilliseconds))
                .Select(h => $"{h.Id} to {h.To}, sent {h.Since}"),
        ];
    }

    public string? DaemonRestartNote() =>
        DaemonClaims.SupportOf(graph.Daemon) == ClaimSupport.Older
            ? "the running ah daemon is older than this install: it would deliver relayed mail a second time as a plugin prompt, so new mail is sent without claim records until it is restarted; restart it with `ahd restart`"
            : null;

    public string? DaemonUnansweredNote() =>
        DaemonClaims.SupportOf(graph.Daemon) == ClaimSupport.Unanswered
            ? "the running ah daemon did not answer the mail protocol check in time (it may be busy); new mail is sent without claim records until it does"
            : null;

    public string? DaemonReadModelNote() => DaemonClaims.ReadModelOf(graph.Daemon);

    public (string Level, string Detail) AhdHeal() =>
        Ah.DaemonClient.DaemonClientModule.HealDoctorRow(_files, graph.Io.Processes, environment.AhHome, graph.Io.Clock.UtcNow);

    public (string Level, string Detail) StateDb()
    {
        var health = Ah.StateDb.StateDbModule.CreateBackups().Health(environment.AhHome, graph.Io.Clock.UtcNow, Ah.StateDb.BackupSettings.Default);
        return (health.Level, health.Detail);
    }

    public string? ConventionsNote(string cwd)
    {
        var loaded = ConfigModule.CreateConventions(graph.Runner, _files, HomeOrNull).LoadConventions(cwd);
        return loaded.FromV1Path
            ? $"this repository's conventions are read from ah v1's path, .claude/ah-conventions.json: commit the file at {loaded.Path}"
            : null;
    }

    private string? HomeOrNull()
    {
        try
        {
            return environment.HomeDirectory;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> DesignSources()
    {
        var configured = environment.GetVariable("CLAUDE_CONFIG_DIR");
        var claude = string.IsNullOrEmpty(configured) ? Path.Combine(HomeOrNull() ?? string.Empty, ".claude") : configured;
        bool Has(string relative) => _files.Exists(Path.Combine(claude, relative));
        var present = new List<string>();
        if (Has("skills/frontend-design"))
        {
            present.Add("frontend-design");
        }

        if (Has("skills/web-design-guidelines") || Has("skills/composition-patterns"))
        {
            present.Add("vercel-labs");
        }

        if (Has("plugins/cache/interfaces/interfaces") || Has("skills/better-interface"))
        {
            present.Add("jakubkrehel");
        }

        if (Has("plugins/cache/addy-web-quality-skills/web-quality-skills") || Has("skills/web-quality-audit"))
        {
            present.Add("addyosmani");
        }

        return present;
    }

    public (string Level, string Detail) TestBucket()
    {
        var table = Ah.Platform.PlatformModule.CreateProcessTable(graph.Runner);
        const double limitSeconds = 3600;
        var now = graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
        var b = Ah.Watch.WatchModule.BucketStatusOf(_files, environment, table, now, limitSeconds * 1000);
        var text = FormattableString.Invariant($"{b.Capacity} tokens, {b.HeldTokens} held by {b.Runs} runs, {b.Waiting} waiting");
        var registry = Ah.Watch.WatchModule.TestRegistryInfoFor(environment.HomeDirectory, Directory.GetCurrentDirectory(), dir => graph.Trust.IsTrusted(Ah.Abstractions.TrustFile.HierarchyDirs, dir));
        text += "; test rows: " + string.Join(", ", registry.Layers.Select(static l => FormattableString.Invariant($"{l.Name} {l.Rows}")));
        var problems = registry.Issues.Select(static i => $"{i.File}: {i.Reason}").Concat(registry.Adapters.Where(static a => a.Contains("ignored", StringComparison.Ordinal))).ToList();
        text += string.Concat(registry.Adapters.Where(static a => !a.Contains("ignored", StringComparison.Ordinal)).Select(static a => "; " + a));
        if (b.DeadLeases > 0)
        {
            problems.Add(FormattableString.Invariant($"{b.DeadLeases} lease(s) whose holder is gone are not yet reaped (the next waiter reaps them)"));
        }

        if (b.OldWaiter)
        {
            problems.Add(FormattableString.Invariant($"a waiter has waited longer than {limitSeconds} s"));
        }

        return problems.Count == 0 ? ("ok", text) : ("warn", $"{text}; {string.Join("; ", problems)}");
    }

    public string GitPorcelain(string directory)
    {
        var result = graph.Runner.Run(new ProcessRequest(["git", "status", "--porcelain"], directory, null, default, TimeSpan.FromSeconds(15)));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git status failed in {directory}");
        }

        return Utf8Text.Decode(result.Stdout.Span);
    }
}
