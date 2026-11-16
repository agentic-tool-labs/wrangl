using Ah.Core.Json;

namespace Ah.Watch;

internal static partial class ViewBuilder
{
    // The menu of a node as data. Every layer binds to the same stable ids; a layer decides how to draw an item and which
    // terminal-only ones it can run. Nothing here is executed by the daemon: an owner-only verb is only ever offered as a command to
    // hand to the Orchestrator. A key binds one action per target: the same key on a tile and on a graph node can mean two things.

    private static JsonObject Action(string id, string label, string? key, string group, string kind, string target, string help, JsonObject invoke, string? confirm = null, string? altKey = null)
    {
        var action = new JsonObject
        {
            ["id"] = id,
            ["label"] = label,
            ["key"] = key is null ? JsonValue.Null : key,
            ["group"] = group,
            ["kind"] = kind,
            ["target"] = target,
            ["help"] = help,
        };
        if (altKey is not null)
        {
            action["alt_key"] = altKey;
        }

        if (confirm is not null)
        {
            action["confirm"] = confirm;
        }

        action["invoke"] = invoke;
        return action;
    }

    private static JsonObject ReadAction(string id, string label, string? key, string group, string target, string help, string route) =>
        Action(id, label, key, group, "read", target, help, new JsonObject { ["get"] = route });

    private static JsonObject CopyAction(string id, string label, string? key, string target, string help, string text) =>
        Action(id, label, key, "copy", "copy", target, help, new JsonObject { ["copy"] = text });

    private static JsonObject AskAction(string id, string label, string? key, string target, string help, string command) =>
        Action(
            id,
            label,
            key,
            "ask",
            "owner",
            target,
            help,
            new JsonObject { ["ask_orchestrator"] = new JsonObject { ["command"] = command, ["request_text"] = $"Please run this for me and tell me what it prints: {command}" } },
            "Only the Orchestrator can run this. This copies the command; nothing is run from here.");

    private static JsonObject NodeViewAction(string id, string label, string key, string help, string verb, string path) =>
        Action(id, label, key, "graph", "view", "graph_node", help, new JsonObject { ["view"] = verb, ["target"] = path });

    /// <summary><paramref name="value"/> as one shell word: unchanged when it holds only safe characters, otherwise single-quoted.</summary>
    private static string Q(string value) =>
        value.Length > 0 && value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '@' or '/' or ':' or '+' or '=' or ',')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string TeamFlag(TeamEntry team) => team.Name is null ? string.Empty : $" --team {Q(team.Name)}";

    private static BuiltView ActionsDocument(ModelCtx c, Node node, string fullPath)
    {
        var actions = new List<JsonObject>();
        switch (node.Kind)
        {
            case "orchestrator":
                OrchestratorActions(node, actions);
                break;
            case "team":
                TeamActions(node, actions);
                break;
            case "stream":
                StreamActions(c, node, actions);
                break;
            case "member":
                MemberActions(c, node, actions);
                break;
            case "mail":
                MailActions(c, node, actions);
                break;
            case "subagent":
                actions.Add(CopyAction("subagent.copy_id", "Copy id", "y", "subagent", "Copy this subagent's id.", node.Run!.AgentId));
                actions.Add(NodeViewAction("node.highlight_edges", "Highlight edges", "H", "Highlight the edges touching this node.", "highlight_edges", node.Path));
                break;
        }

        var list = new JsonArray();
        foreach (var a in actions)
        {
            list.Add(a);
        }

        var view = ModelEnvelope(c, "actions", node, fullPath);
        view["summary"] = new JsonObject { ["actions"] = list.Count };
        view["children"] = new JsonArray();
        view["body"] = new JsonObject { ["node"] = new JsonObject { ["kind"] = node.Kind, ["path"] = node.Path }, ["actions"] = list };
        return Finish(view);
    }

    private static void OrchestratorActions(Node node, List<JsonObject> actions)
    {
        var orch = node.Orch!;
        var path = node.Path;
        actions.Add(ReadAction("orchestrator.detail", "Detail", "enter", "open", "graph_node", "Show everything known about this orchestrator.", "/detail" + path));
        actions.Add(ReadAction("orchestrator.explode", "Explode into graph", "enter", "open", "orchestrator", "Open this orchestrator as a graph of its team.", "/graph" + path));
        actions.Add(ReadAction("orchestrator.status", "Team status", "S", "read", "orchestrator", "Show the status of every team.", "/status" + path));
        actions.Add(ReadAction("orchestrator.mail", "Show waiting mail", "M", "read", "orchestrator", "Show the mail waiting in this orchestrator's box.", "/inbox" + path));
        actions.Add(NodeViewAction("node.plan", "Plan", "P", "Show this orchestrator's plan: what is done, in which release, what is in progress and what is left.", "switch.plan", path));
        actions.Add(NodeViewAction("node.show_subagents", "Show subagents", "a", "Show or hide the subagents running under this node.", "show_subagents", path));
        actions.Add(NodeViewAction("node.highlight_edges", "Highlight edges", "H", "Highlight the edges touching this node.", "highlight_edges", path));
        actions.Add(CopyAction("orchestrator.copy_repo", "Copy repo path", "y", "orchestrator", "Copy the path of the repo this orchestrator works in.", orch.Dir));
        if (orch.Pid is { } pid)
        {
            actions.Add(CopyAction("orchestrator.copy_pid", "Copy pid", "Y", "orchestrator", "Copy this orchestrator's process id.", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var named = orch.Teams.Where(static t => t.Name is not null).ToList();
        if (named.Count <= 1)
        {
            actions.Add(AskAction("orchestrator.ask_snapshot", "Ask Orchestrator: snapshot…", null, "orchestrator", "Copy a request asking the Orchestrator to snapshot the team.", "ah roster snapshot" + (named.Count == 1 ? TeamFlag(named[0]) : string.Empty)));
        }
        else
        {
            foreach (var team in named)
            {
                actions.Add(AskAction($"orchestrator.ask_snapshot.{Seg(team.Tkey)}", $"Ask Orchestrator: snapshot {team.Name}…", null, "orchestrator", $"Copy a request asking the Orchestrator to snapshot team {team.Name}.", "ah roster snapshot" + TeamFlag(team)));
            }
        }
    }

    private static void TeamActions(Node node, List<JsonObject> actions)
    {
        var team = node.Team!;
        var flag = TeamFlag(team);
        actions.Add(ReadAction("team.status", "Team status", "s", "read", "team", "Show the status of this team.", "/status" + node.Path));
        actions.Add(ReadAction("team.stream_board", "Stream board", "S", "read", "team", "Show the streams of this team and their state.", "/stream-status" + node.Path));
        actions.Add(CopyAction("team.copy_name", "Copy team name", "y", "team", "Copy this team's name.", team.Key));
        actions.Add(AskAction("team.ask_disband", "Ask: disband…", null, "team", "Copy a request asking the Orchestrator for the plan to disband this team.", $"ah roster disband --plan{flag}"));
    }

    private static void StreamActions(ModelCtx c, Node node, List<JsonObject> actions)
    {
        var stream = node.Stream!;
        var body = StreamBody(c.Src, node.Facts!, stream);
        actions.Add(ReadAction("stream.status", "Stream status", "S", "read", "stream", "Show the state of this stream.", "/stream-status" + node.Path));
        if (body["branch"].IsText)
        {
            actions.Add(CopyAction("stream.copy_branch", "Copy branch", "y", "stream", "Copy this stream's branch name.", body["branch"].AsString()));
        }

        if (body.Obj("worktree")!["path"].IsText)
        {
            actions.Add(CopyAction("stream.copy_worktree", "Copy work tree path", "Y", "stream", "Copy the path of this stream's work tree.", body.Obj("worktree")!["path"].AsString()));
        }

        actions.Add(AskAction("stream.ask_done", "Ask: stream-done…", null, "stream", "Copy a request asking the Orchestrator to end this stream.", $"ah roster stream-done {Q(stream.Name)}{TeamFlag(node.Team!)}"));
    }

    private static void MemberActions(ModelCtx c, Node node, List<JsonObject> actions)
    {
        var team = node.Team!;
        var member = node.Member!;
        var name = member.Str("name")!;
        var path = node.Path;
        var flag = TeamFlag(team);
        actions.Add(ReadAction("member.detail", "Detail", "enter", "open", "member", "Show everything known about this member.", "/detail" + path));
        actions.Add(ReadAction("member.mail", "Show mail", "M", "read", "member", "Show the mail in this member's box.", "/inbox" + path));
        actions.Add(NodeViewAction("node.show_subagents", "Show subagents", "a", "Show or hide the subagents running under this node.", "show_subagents", path));
        actions.Add(NodeViewAction("node.highlight_edges", "Highlight edges", "H", "Highlight the edges touching this node.", "highlight_edges", path));
        actions.Add(CopyAction("member.copy_name", "Copy name", "y", "member", "Copy this member's name.", name));
        if (PlaceOf(c.Src, c.Registrations, member).Pid is { Kind: JsonKind.Number } pid)
        {
            actions.Add(CopyAction("member.copy_pid", "Copy pid", "Y", "member", "Copy this member's process id.", JsString(pid)));
        }

        if (member.Str("session_id") is { Length: > 0 } session)
        {
            actions.Add(CopyAction("member.copy_session", "Copy session id", null, "member", "Copy this member's session id.", session));
        }

        actions.Add(AskAction("member.ask_dismiss", "Ask: dismiss…", "D", "member", "Copy a request asking the Orchestrator for the plan to dismiss this member.", $"ah roster dismiss {Q(name)} --plan{flag}"));
    }

    private static void MailActions(ModelCtx c, Node node, List<JsonObject> actions)
    {
        var entry = node.Entry!;
        var path = node.Path;
        if (entry.Str("ref") is { Length: > 0 } file)
        {
            actions.Add(ReadAction("mail.open_text", "Open text", "enter", "open", "mail", "Show the request text.", path + "/text"));
            if (MsgPointer.Handle(file) is { } handle)
            {
                actions.Add(CopyAction("mail.copy_show", "Copy show command", "y", "mail", "Copy the command that prints this message", $"ah msg show {handle}"));
            }
        }

        actions.Add(CopyAction("mail.copy_inbox", "Copy inbox command", "Y", "mail", "Copy the command that lists the mail in this box.", $"ah msg inbox --all --address {Q(AddressOf(node.Team!, node.BoxOwner!))}"));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var team in node.Orch!.Teams)
        {
            foreach (var m in c.FactsOf(node.Orch, team).Members)
            {
                _ = names.TryAdd(m.Str("name")!, c.MPath(node.Orch, team, m.Str("name")!));
            }
        }

        if (NodeOfAddress(c, node.Orch, names, entry.Str("from")) is { } sender)
        {
            actions.Add(ReadAction("mail.show_sender", "Show sender", "s", "read", "mail", "Show the node that sent this mail.", "/detail" + sender));
        }

        actions.Add(ReadAction("mail.show_receiver", "Show receiver", "t", "read", "mail", "Show the node whose box holds this mail.", "/detail" + (node.BoxOwner == "orchestrator" ? c.OPath(node.Orch) : node.Holder)));
    }
}
