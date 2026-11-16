using Ah.Abstractions;
using Ah.Config;
using Ah.Hierarchy;

namespace Ah.Mesh;

public static partial class MeshModule
{
    /// <summary>
    /// The participation contract: its JSON document and markdown guide, built from the route table and constants the daemon
    /// enforces and the prose file at <c>hooks/participation-contract.md</c> under the plugin root.
    /// </summary>
    public static IContractCore CreateContract(IFileReader files, IAhEnvironment environment) => new ContractCore(files, environment);

    /// <summary>The team and join lookups a participant makes before it registers.</summary>
    public static IContractLookups CreateContractLookups(IMailbox mailbox, IHierarchyCore hierarchy, IConfigCore config) =>
        new ContractLookups(mailbox, hierarchy, config);

    /// <summary>The record notes of link commands, written through the mesh sender's mirror under <paramref name="context"/>.</summary>
    public static ILinkMirror CreateLinkMirror(
        IMailbox mailbox,
        IMeshRule rule,
        ILinks links,
        HierarchyServices hierarchy,
        IClock clock,
        IStateWriter state,
        IFileReader files,
        RequestContext context,
        IRunFacts? runFacts = null)
    {
        var concrete = mailbox as Mailbox ?? throw new ArgumentException("not a mailbox made by CreateMailbox", nameof(mailbox));
        return new MeshLinkMirror(new MeshSend(concrete, rule, links, hierarchy, clock, state, null, runFacts), context);
    }

    /// <summary>
    /// Join requests and their decisions over the record machinery of <see cref="CreateLinkCommands"/>; <paramref name="effects"/>
    /// performs the writes, <paramref name="mirror"/> the record notes.
    /// </summary>
    public static IJoinCommands CreateJoinCommands(
        IMeshRule rule,
        ILinks links,
        IHierarchyCore core,
        IHierarchyMessages messages,
        IFileReader files,
        IClock clock,
        IMailbox mailbox,
        IContractLookups lookups,
        IConfigCore config,
        IAhEnvironment environment,
        IProcessInspector processes,
        IJoinEffects effects,
        ILinkMirror mirror,
        IRunFacts? runFacts = null) =>
        new JoinCommands(
            new LinkCommands(rule, links, core, messages, files, clock, effects, mirror, runFacts),
            mailbox, lookups, core, new MailCore(clock), config, environment, processes, effects, clock);

    /// <summary>The role-contract lookup over the plugin's <c>contracts/</c> texts.</summary>
    public static IContractRoles CreateContractRoles(IConfigCore config, IRoleContracts contracts) => new ContractRoles(config, contracts);

    /// <summary>
    /// The link commands (<c>link-request</c> and <c>link grant|deny|revoke|extend-request|extend|list|show|post</c>) over the writes
    /// of one caller: <paramref name="effects"/> performs the <c>links</c> operations, message creation and delivery,
    /// <paramref name="mirror"/> the record notes.
    /// </summary>
    public static ILinkCommands CreateLinkCommands(
        IMeshRule rule,
        ILinks links,
        IHierarchyCore core,
        IHierarchyMessages messages,
        IFileReader files,
        IClock clock,
        ILinkEffects effects,
        ILinkMirror mirror,
        IRunFacts? runFacts = null) => new LinkCommands(rule, links, core, messages, files, clock, effects, mirror, runFacts);

    /// <summary>
    /// Registers every mesh route: the participation contract and its role and team lookups, <c>POST /v1/send</c>, and the join
    /// request and its poll.
    /// </summary>
    public static void Register(IRouteRegistry routes, MeshRouteDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(dependencies);
        MeshRoutes.Register(routes, dependencies);
    }

    /// <summary>Registers only the routes that read the plugin's contract files: <c>GET /v1/contract</c>, its guide and its role.</summary>
    public static void RegisterContractRoutes(IRouteRegistry routes, IContractCore contract, IContractRoles roles)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(roles);
        MeshRoutes.RegisterContract(routes, contract, roles);
    }

    /// <summary>The count of acks for entries no pull returned, shared by the mailbox routes and the daemon's health.</summary>
    public static IAckTally CreateAckTally() => new AckTally();

    /// <summary>The processes one daemon has heard since it started, shared by the mailbox routes and the bare recovery's signal.</summary>
    public static IHeardProcesses CreateHeardProcesses() => new HeardProcesses();

    /// <summary>
    /// Registers the mailbox routes: <c>GET /v1/inbox</c>, <c>POST /v1/ack</c>, <c>POST /v1/register</c> and the box, entry and
    /// registration views under <c>/v1/inspect</c>.
    /// </summary>
    public static void RegisterMailboxRoutes(IRouteRegistry routes, MailboxRouteDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(dependencies);
        MeshRoutes.RegisterMailbox(routes, dependencies);
    }

    /// <summary>
    /// The request <c>ah msg due</c> sends to find out whether a daemon serves <c>GET /v1/due</c>: a daemon that has the route
    /// answers it 400 (the request names no session), one that does not answers 404.
    /// </summary>
    public const string DueProbePath = "/v1/due?wait=1&in=%7B%7D";

    /// <summary>
    /// Registers <c>GET /v1/due</c>: what the Claude mod's mail poll would hand over, held open until something is due or the wait
    /// (1 to 25 s) ends. The registration and each box are re-checked every 200 ms and the answer is recomputed only when one changed
    /// or a grace or resubmit moment has passed.
    /// </summary>
    public static void RegisterDueRoute(IRouteRegistry routes, IMailDue due, IMailbox mailbox, IFileReader files, IDueTimer? timer = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(due);
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(files);
        MeshRoutes.RegisterDue(routes, due, mailbox, files, timer, log);
    }

    /// <summary>The read-only mailbox views behind <c>/v1/inspect</c> and the CLI's file fallback.</summary>
    public static IMailboxInspect CreateMailboxInspect(IMailbox mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return new MailboxInspect(mailbox);
    }
}
