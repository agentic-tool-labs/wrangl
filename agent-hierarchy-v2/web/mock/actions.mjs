// The action lists the daemon's ViewBuilder serves for a member, an orchestrator and the root, in the same document shape
// ({node: {kind, path}, actions}); the texts follow the TUI's lists.

const act = (id, label, key, group, kind, target, help, invoke, extra = {}) => ({ id, label, key, group, kind, target, help, ...extra, invoke });

const ASK = "Only the Orchestrator can run this. This copies the command; nothing is run from here.";
const ask = (command) => ({ ask_orchestrator: { command, request_text: `Please run this for me and tell me what it prints: ${command}` } });

export function memberActions({ name, path, pid, session, team }) {
  return {
    node: { kind: "member", path },
    actions: [
      act("member.detail", "Detail", "enter", "open", "read", "member", "Show everything known about this member.", { get: `/detail${path}` }),
      act("member.mail", "Show mail", "M", "read", "read", "member", "Show the mail in this member's box.", { get: `/inbox${path}` }),
      act("node.show_subagents", "Show subagents", "a", "graph", "view", "graph_node", "Show or hide the subagents running under this node.", { view: "show_subagents", target: path }),
      act("node.highlight_edges", "Highlight edges", "H", "graph", "view", "graph_node", "Highlight the edges touching this node.", { view: "highlight_edges", target: path }),
      act("member.copy_name", "Copy name", "y", "copy", "copy", "member", "Copy this member's name.", { copy: name }),
      ...(pid == null ? [] : [act("member.copy_pid", "Copy pid", "Y", "copy", "copy", "member", "Copy this member's process id.", { copy: String(pid) })]),
      ...(session == null ? [] : [act("member.copy_session", "Copy session id", null, "copy", "copy", "member", "Copy this member's session id.", { copy: session })]),
      act("member.ask_dismiss", "Ask: dismiss…", "D", "ask", "owner", "member", "Copy a request asking the Orchestrator for the plan to dismiss this member.", ask(`ah roster dismiss ${name} --plan --team ${team}`), { confirm: ASK }),
    ],
  };
}

export function orchestratorActions({ path, pid, team }) {
  return {
    node: { kind: "orchestrator", path },
    actions: [
      act("orchestrator.detail", "Detail", "enter", "open", "read", "graph_node", "Show everything known about this orchestrator.", { get: `/detail${path}` }),
      act("orchestrator.explode", "Explode into graph", "enter", "open", "read", "orchestrator", "Open this orchestrator as a graph of its team.", { get: `/graph${path}` }),
      act("orchestrator.status", "Team status", "S", "read", "read", "orchestrator", "Show the status of every team.", { get: `/status${path}` }),
      act("orchestrator.mail", "Show waiting mail", "M", "read", "read", "orchestrator", "Show the mail waiting in this orchestrator's box.", { get: `/inbox${path}` }),
      act("node.plan", "Plan", "P", "graph", "view", "graph_node", "Show this orchestrator's plan: what is done, in which release, what is in progress and what is left.", { view: "switch.plan", target: path }),
      act("node.show_subagents", "Show subagents", "a", "graph", "view", "graph_node", "Show or hide the subagents running under this node.", { view: "show_subagents", target: path }),
      ...(pid == null ? [] : [act("orchestrator.copy_pid", "Copy pid", "Y", "copy", "copy", "orchestrator", "Copy this orchestrator's process id.", { copy: String(pid) })]),
      act("orchestrator.ask_snapshot", "Ask Orchestrator: snapshot…", null, "ask", "owner", "orchestrator", "Copy a request asking the Orchestrator to snapshot the team.", ask(`ah roster snapshot --team ${team}`), { confirm: ASK }),
    ],
  };
}

export function rootActions() {
  return {
    node: { kind: "root", path: "/" },
    actions: [
      act("view.refresh", "Refresh", "r", "view", "view", "sky", "Read the hierarchy again now.", { view: "refresh" }),
      act("view.theme", "Change theme", "T", "view", "view", "sky", "Cycle through the colour themes.", { view: "theme" }),
      act("view.help", "Help", "?", "view", "view", "sky", "Show the keys and what they do.", { view: "help" }),
      act("view.quit", "Quit", "q", "view", "view", "sky", "Leave the viewer.", { view: "quit" }),
    ],
  };
}
