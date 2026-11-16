using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Hooks.Tests;

/// <summary>Fakes and fixtures the hook tests share.</summary>
public sealed class BindingAttestationTests
{
    internal sealed class Callers(
        IReadOnlyList<CallerBox>? registered = null, Action<CallerRequest>? onRegister = null, Action<RebindRequest>? onRebind = null, Func<double, string?, bool>? processGone = null, IReadOnlyList<DeciderBox>? deciders = null) : IMailCallers
    {
        public bool OrchestratorProcessIsGone(double pid, string? recordedStart) => processGone?.Invoke(pid, recordedStart) ?? false;

        public RequestContext RequestContext(string cwd) => throw new NotSupportedException();

        public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request) => registered ?? throw new NotSupportedException();

        public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request)
        {
            onRegister?.Invoke(request);
            return registered ?? throw new NotSupportedException();
        }

        public IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request) => RegisterCaller(request);

        public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request) => throw new NotSupportedException();

        public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null) => deciders ?? [];

        public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null) => throw new NotSupportedException();

        public string? ProcessCommand(int pid) => null;

        public RebindResult RebindOwnedTeams(RebindRequest request)
        {
            onRebind?.Invoke(request);
            return new RebindResult([], []);
        }
    }

    internal sealed class Daemon : IDaemonClient
    {
        internal List<DaemonRequest> Requests { get; } = [];

        internal bool Up { get; set; } = true;

        internal TimeSpan? DiscoverDelay { get; set; }

        internal Exception? Fault { get; set; }

        public async Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (Fault is not null)
            {
                throw Fault;
            }

            if (DiscoverDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return Up ? new DaemonInfo(1, 1, "9.9.9", 1, "/tmp/ahd.sock", "/tmp", "now") : null;
        }

        public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default) => throw new NotSupportedException("a hook never starts the daemon");

        internal int EnsureCalls { get; private set; }

        public Task<DaemonInfo?> EnsureDaemonForHookAsync(TimeSpan budget, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            return EnsureDaemonAsync(budget, cancellationToken);
        }

        public Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new DaemonReply(200, default));
        }

        public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RecordFallback(string verb, string reason)
        {
        }
    }

    internal const string Start = "Mon Oct 12 08:00:00 2026";

    /// <summary>
    /// The registration file a hook leaves: written to disk directly because the test rig's state writer records and writes nothing.
    /// </summary>
    internal static void Register(IMailbox mailbox, string session, int pid, string? start, string address = "orchestrator@tm")
    {
        var file = mailbox.RegistrationFile(session);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var record = new JsonObject
        {
            ["session_id"] = session,
            ["address"] = address,
            ["pid"] = pid,
            ["boxes"] = new JsonObject { [address] = "/ah/mailbox/tm~aaaaaaaaaa/" + address.Split('@')[0] },
        };
        if (start is not null)
        {
            record["pid_start"] = start;
        }

        File.WriteAllBytes(file, JsonText.ToUtf8(record, 0));
    }

    internal static IMailbox MailboxFor(HooksHostTests.Rig rig)
    {
        var io = PlatformModule.CreateIo(null);
        return MeshModule.CreateMailbox(io.Files, rig.Services.Environment, rig.Services.Clock, io.Random, rig.Services.Hierarchy, rig.Services.Roster, rig.Writer);
    }

    internal static readonly IReadOnlyList<CallerBox> Orchestrator = [new("orchestrator@tm", "/ah/mailbox/tm~aaaaaaaaaa/orchestrator")];

    internal static readonly IReadOnlyList<CallerBox> Member = [new("impl@tm", "/ah/mailbox/tm~aaaaaaaaaa/impl")];
}
