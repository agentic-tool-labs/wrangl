using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

/// <summary>The link and join verbs: <c>link-request</c>, <c>link ...</c> and <c>join accept|deny</c>.</summary>
internal sealed partial class MsgCommand
{
    private int LinkRequestOrLink()
    {
        var ctx = _graph.Callers.RequestContext(Where);
        var effects = CallerEffects(ctx);
        var commands = MeshModule.CreateLinkCommands(
            MeshModule.CreateMeshRule(_graph.Links, _graph.Environment),
            _graph.Links,
            _graph.Hierarchy.Core,
            _graph.Hierarchy.Messages,
            _graph.Io.Files,
            _graph.Io.Clock,
            effects,
            effects,
            _graph.RunFacts);
        var request = new LinkCommandRequest(
            _cmd,
            _opts.Positional,
            _opts.TextValues(),
            new LinkContext(ctx.HierarchyDirectory, ctx.MainHierarchyDirectory),
            _graph.Mailbox.TeamLookup(ctx),
            WriteAddresses());
        var result = Unwrap(commands.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        ChecklistLinked();
        return Print(result.Result, result.Text, 0);
    }

    private int JoinDecision()
    {
        var ctx = _graph.Callers.RequestContext(Where);
        var effects = CallerEffects(ctx);
        var commands = MeshModule.CreateJoinCommands(
            MeshModule.CreateMeshRule(_graph.Links, _graph.Environment),
            _graph.Links,
            _graph.Hierarchy.Core,
            _graph.Hierarchy.Messages,
            _graph.Io.Files,
            _graph.Io.Clock,
            _graph.Mailbox,
            MeshModule.CreateContractLookups(_graph.Mailbox, _graph.Hierarchy.Core, _graph.Config),
            _graph.Config,
            _graph.Environment,
            _graph.Io.Processes,
            effects,
            effects);
        var deciders = _graph.Callers.JoinDeciderBoxes(Where, SendingSession).Select(d => new JoinDecider(d.Address, d.Session)).ToList();
        var outcome = commands.CommandAsync(new JoinCommandRequest(ctx, _opts.Positional, _opts.TextValues(), deciders), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (outcome.Failure is { } failure)
        {
            throw new MailFailure(failure.Code, failure.Message);
        }

        return Print(outcome.Value!.Result, outcome.Value.Text, 0);
    }

    /// <summary>The writes of a link or join command, made under this caller's context and session.</summary>
    private MeshEffects CallerEffects(RequestContext ctx) => new(_graph, new RequestScope(), ctx, SendingSession, SendEntry);
}
