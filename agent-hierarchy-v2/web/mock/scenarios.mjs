// Hand-written mock teams for screenshots that look like real use. AH_MOCK_FIXTURE=team or =lanes selects one; fixture.mjs swaps
// its own tables for the scenario's. Not PRESETS rows, so the E4 harness never measures them.
//   team   one team, no sub-orchestrators: an orchestrator and six members in three streams of one to three
//   busy   the owner's Flow view: names that carry the team's prefix, six busy implementors, a reviewer two columns from the lead
//   lanes  a top team (orchestrator, architect, reviewer, qa) and two sub-orchestrator lanes, each a child team of three

// Exchange rows: id, slug, from, to, minutes ago created, response minutes ago | null, parent, plan item | null, title, brief, report.
// A slug that names a plan stream counts toward item-msgs.

const TEAM = {
  run: "msg-board",
  // ah-ui-ia U2: one decision waiting (the first open question), and one stuck open task (exchange id, minutes since its last activity)
  decisions: 1,
  stuck: [["20261031-1254-td18", 52]],
  pipelineItems: [{ slug: "msg-delivery", rounds: 2, round_cap: 3 }, { slug: "board-icicle", rounds: 1, round_cap: 3 }],
  // The team's `resources` (ah2-509): name, role, who, minutes since, note, outside. One hold with a waiter, and a
  // wait on a name held outside the team.
  resources: [
    ["port-5173", "hold", "implementor-1", 18, "mock dev server", false],
    ["port-5173", "wait", "qa", 6, "e2e run", false],
    ["gpu-runner", "hold", "release/implementor-3", 42, "bench", true],
    ["gpu-runner", "wait", "ux-engineer", 11, "screenshots", false],
  ],
  // name, role, state, tool, minutes since the tool, stream branch
  members: [
    ["architect", "architect", "idle", null, 14, "ah/liveness"],
    ["implementor-1", "implementor", "working", "Edit", 0, "ah/msg-delivery"],
    ["reviewer", "reviewer", "working", "Read", 1, "ah/msg-delivery"],
    ["qa", "qa", "working", "Bash", 2, "ah/msg-delivery"],
    ["ux-engineer", "ux-engineer", "working", "Edit", 0, "ah/board-icicle"],
    ["implementor-2", "implementor", "blocked", null, 9, "ah/board-icicle"],
  ],
  context: {
    architect: ["claude-opus-4-5", 96_400],
    "implementor-1": ["claude-sonnet-4-5", 141_200],
    reviewer: ["claude-opus-4-5", 63_900],
    qa: ["claude-haiku-4-5", 22_300],
    "ux-engineer": ["claude-sonnet-4-5", 118_700],
    "implementor-2": ["claude-sonnet-4-5", 87_500],
  },
  working1h: { architect: 0.34, "implementor-1": 0.81, reviewer: 0.42, qa: 0.57, "ux-engineer": 0.73, "implementor-2": 0.18 },
  eta: { "msg-delivery": "large", "board-icicle": "large", liveness: "large" },
  streams: [
    { stream: "msg-delivery", owner: "implementor-1", branch: "ah/msg-delivery", goal: "Reports delivered exactly once, to the right address" },
    { stream: "board-icicle", owner: "ux-engineer", branch: "ah/board-icicle", goal: "Epics and deliverables on the board as an icicle" },
    { stream: "liveness", owner: "architect", branch: "ah/liveness", goal: "One liveness truth for every reader" },
  ],
  // stream, item, title, state, holder, rank
  plan: [
    ["msg-delivery", "md1", "Exactly-once report delivery", "in_progress", "implementor-1", 1],
    ["msg-delivery", "md2", "Survive a session rename mid-send", "next", null, 2],
    ["msg-delivery", "md3", "Close finished exchanges, notes included", "done", null, 3],
    ["msg-delivery", "md4", "Refuse team_mismatch at send time", "done", null, 4],
    ["msg-delivery", "md5", "Delivery regression suite", "in_progress", "qa", 5],
    ["board-icicle", "bi1", "Icicle layout for epics", "in_progress", "ux-engineer", 1],
    ["board-icicle", "bi2", "Deliverable rows and rollups", "blocked", "implementor-2", 2],
    ["board-icicle", "bi3", "Keyboard focus through the icicle", "not_started", null, 3],
    ["board-icicle", "bi4", "Kind and parent in the board model", "done", null, 4],
    ["liveness", "lv1", "One liveness spec", "in_progress", "architect", 1],
    ["liveness", "lv2", "Pid reuse and reboot cases", "next", null, 2],
    ["liveness", "lv3", "Fold /v1/view onto the read model", "not_started", null, 3],
  ],
  checklist: {
    md1: { role: "implementor", todo: "dedupe on request id; rename case left", refs: ["20261031-1302-td14"], stateMin: 58, updatedMin: 4 },
    bi2: { role: "implementor", todo: "wait for the E2 parent field", stateMin: 101, updatedMin: 38 },
    lv1: { role: "architect", todo: "spec r2: reboot and pid reuse", stateMin: 66, updatedMin: 14 },
  },
  // Backlog kind/parent (GROOM keys) and the deliverable each stream's checklist items roll up to, in item order.
  epics: {
    backlog: {
      b8: { kind: "epic" },
      b9: { kind: "epic" },
      b14: { kind: "deliverable", parent: "b8" },
      b13: { kind: "deliverable", parent: "b8" },
      b20: { kind: "deliverable", parent: "b9" },
      b7: { kind: "deliverable", parent: "b9" },
      b12: { parent: "b14" },
    },
    checklist: { "msg-delivery": ["b14", "b14", "b13", "b14", "b13"], "board-icicle": ["b20", "b20", "b20", "b20"], liveness: ["b7", "b7", "b7"] },
  },
  exchanges: [
    ["20261031-1105-td01", "liveness", "orchestrator", "architect", 175, 140, null, "lv1", "Spec: one liveness truth", "Write the liveness spec: one implementation feeding /v1/view, the read model, hooks and the UI. Cover live, idle, busy, dead and bare.", "Spec written at docs/specs/liveness.md. Three open edges flagged: pid reuse, reboot, a bare session."],
    ["20261031-1106-td02", "msg-delivery", "orchestrator", "implementor-1", 172, 166, null, null, "Confirm the delivery scope", "Read the delivery spec and confirm the five items and their order.", "Scope confirmed: five items. md2 depends on md1; md4 first, it is the smallest."],
    ["20261031-1107-td03", "board-icicle", "orchestrator", "ux-engineer", 171, 162, null, null, "Confirm the icicle scope", "Read the board icicle spec and confirm the tokens and the views it touches.", "Scope confirmed. Tokens from r6; Plans and the backlog board change, nothing else."],
    ["20261031-1110-td04", "msg-delivery", "orchestrator", "implementor-1", 168, 118, null, "md4", "Refuse team_mismatch at send time", "Validate the target team in msg send before anything is written. Negative test for a member of another team.", "DONE @4be19c2: send validates the team up front; 6 tests, one for a same-named member of another team."],
    ["20261031-1112-td05", "board-icicle", "orchestrator", "ux-engineer", 166, 120, null, "bi4", "Kind and parent in the board model", "Read kind and parent from the backlog and plan-items slices into the board model. No layout yet.", "DONE @71ac0d3: the model carries kind and parent; an orphan task shows at the top level."],
    ["20261031-1130-td06", "msg-delivery", "implementor-1", "qa", 150, 141, "20261031-1110-td04", "md4", "Run the messaging suites @4be19c2", "Run Core and the messaging shell suites at 4be19c2; report failures only.", "PASS: Core 1,204 and messaging 318, 0 failed."],
    ["20261031-1135-td07", "liveness", "orchestrator", "reviewer", 135, 110, "20261031-1105-td01", "lv1", "Read-through: liveness spec", "Review docs/specs/liveness.md for gaps a builder would hit.", "VERDICT CHANGES REQUIRED: the reboot path is unspecified, and bare sessions have no state."],
    ["20261031-1142-td08", "msg-delivery", "orchestrator", "reviewer", 133, 124, "20261031-1110-td04", "md4", "Review the team_mismatch guard @4be19c2", "Review 4be19c2 against the spec; classify findings.", "VERDICT PASS WITH NITS: one helper name. No defects."],
    ["20261031-1148-td09", "board-icicle", "orchestrator", "implementor-2", 132, 101, null, "bi2", "Deliverable rows and rollups", "Build the deliverable rows under each epic with done/total rollups from their checklist items.", "BLOCKED: plan-items carries no parent until E2 lands; rollups have nothing to sum."],
    ["20261031-1152-td10", "liveness", "architect", "orchestrator", 128, 126, null, "lv1", "Question: which start time is authoritative?", "pid_start and session_start disagree after a reboot. Which one does liveness trust?", "pid_start wins; session_start is only a hint for a bare session."],
    ["20261031-1202-td11", "msg-delivery", "orchestrator", "implementor-1", 118, 74, null, "md3", "Close finished exchanges, notes included", "An exchange closes when its report lands, and a note reply closes it too.", "DONE @9d03e1a: notes close their exchange; the open list drops them on the next tick."],
    ["20261031-1210-td12", "board-icicle", "ux-engineer", "reviewer", 110, 96, null, "bi1", "Review the icicle tokens", "Review the icicle's colour and type tokens in both themes.", "VERDICT CHANGES REQUIRED: deliverable rows are 3.9:1 in dark; need 4.5:1."],
    ["20261031-1220-td13", "msg-delivery", "qa", "orchestrator", 100, 99, null, null, "Note: a flaky shell suite", "test-ahd-fallback failed once on the shared runner and passed on rerun.", "Noted; quarantine it if it fails again."],
    ["20261031-1230-td24", "board-icicle", "implementor-2", "qa", 90, 84, null, "bi2", "Run the web tests on the rollup branch", "Run the web tests on ah/board-icicle at HEAD.", "PASS: 412 web tests, 0 failed."],
    ["20261031-1245-td15", "board-icicle", "orchestrator", "ux-engineer", 88, null, null, "bi1", "Icicle layout for epics", "Lay out epics, deliverables and items as an icicle on the board, with the contrast fix from review."],
    ["20261031-1246-td16", "msg-delivery", "implementor-1", "reviewer", 74, 58, "20261031-1202-td11", "md3", "Review exchange close @9d03e1a", "Review 9d03e1a; the note-closes-exchange path especially.", "VERDICT PASS."],
    ["20261031-1250-td17", "msg-delivery", "orchestrator", "qa", 70, 40, null, "md5", "Delivery regression suite", "Write a regression suite for delivery: duplicates, rename mid-send, a dead target.", "DONE: 14 scenarios; the rename case is red, as expected until md2."],
    ["20261031-1254-td18", "liveness", "orchestrator", "architect", 66, null, "20261031-1135-td07", "lv1", "Liveness spec r2: reboot and pid reuse", "Amend the spec for the reviewer's two findings: the reboot path and bare sessions."],
    ["20261031-1302-td14", "msg-delivery", "orchestrator", "implementor-1", 58, null, null, "md1", "Exactly-once report delivery", "A report is delivered once even when the sender retries. Negative test: two different reports with the same text both land."],
    ["20261031-1316-td19", "msg-delivery", "implementor-1", "orchestrator", 44, 41, "20261031-1302-td14", "md1", "Question: dedupe on request id or content?", "Dedupe on the request id, or on a hash of the report body?", "The request id: the same text can legitimately be sent twice."],
    ["20261031-1320-td20", "board-icicle", "implementor-2", "orchestrator", 40, 38, "20261031-1148-td09", "bi2", "Question: read parent from the backlog meanwhile?", "May I read parent from the backlog slice until plan-items has it?", "No: wait for E2, it lands today. Pick up bi3 groundwork if idle."],
    ["20261031-1325-td21", "msg-delivery", "orchestrator", "implementor-1", 35, 31, "20261031-1302-td14", "md1", "Status: dedupe", "Where are you on the dedupe?", "Halfway: request-id dedupe is in; the rename case is left."],
    ["20261031-1334-td22", "board-icicle", "ux-engineer", "qa", 30, 26, "20261031-1245-td15", "bi1", "Screenshot both themes at 1440x900", "Screenshot the board in light and dark at 1440x900 and check contrast.", "DONE: 4 PNGs; every row at 4.5:1 or better. The dark board is [e1]; the contrast log is [e2].\n\n## evidence\n- [e1] board-1440x900-dark.png · image/png · 169 KiB · evidence:20261031-1334-td22/e1\n- [e2] contrast.txt · text/plain · 1 KiB · evidence:20261031-1334-td22/e2\n"],
    ["20261031-1338-td23", "msg-delivery", "implementor-1", "qa", 22, 15, "20261031-1302-td14", "md1", "Run the delivery suite on the dedupe branch", "Run the delivery regression suite on ah/msg-delivery at HEAD.", "FAIL: 1 of 14, rename-during-send. The other 13 pass."],
    ["20261031-1340-td25", "liveness", "architect", "reviewer", 20, 11, "20261031-1254-td18", "lv1", "Pre-read: liveness spec r2 draft", "Pre-read the r2 draft before I hand it in.", "Reads clean; one nit on how pid_start is compared."],
    ["20261031-1348-td26", "msg-delivery", "orchestrator", "reviewer", 12, null, "20261031-1250-td17", "md5", "Review the delivery regression suite", "Review the 14 scenarios: do they pin the spec, and can each fail?"],
    ["20261031-1351-td27", "board-icicle", "ux-engineer", "orchestrator", 9, 8, "20261031-1245-td15", "bi1", "Note: icicle draft up on the mock", "The icicle draft is up on the mock; screenshots in the response file.", "Looks right; keep going."],
    ["20261031-1355-td28", "board-icicle", "orchestrator", "implementor-2", 6, null, "20261031-1148-td09", "bi2", "Resume rollups: E2 has landed", "E2 is on the base now; rebase and resume the deliverable rollups."],
    ["20261031-1357-td29", "msg-delivery", "implementor-1", "qa", 3, null, "20261031-1302-td14", "md1", "Rerun the delivery suite after the rename fix", "Rerun the delivery regression suite on ah/msg-delivery at HEAD."],
  ],
  traffic: [
    ["orchestrator", "implementor-1", "msg-delivery"],
    ["implementor-1", "qa", "msg-delivery"],
    ["orchestrator", "ux-engineer", "board-icicle"],
    ["ux-engineer", "reviewer", "board-icicle"],
    ["architect", "orchestrator", "liveness"],
    ["orchestrator", "reviewer", "msg-delivery"],
    ["implementor-2", "orchestrator", "board-icicle"],
  ],
};

const LANES = {
  run: "rc4-lanes",
  // the four open questions of the questions slice
  decisions: 4,
  pipelineItems: [{ slug: "lane-board", rounds: 1, round_cap: 3 }, { slug: "lane-delivery", rounds: 2, round_cap: 3 }],
  members: [
    ["architect", "architect", "working", "Read", 2, null],
    ["reviewer", "reviewer", "idle", null, 18, "ah/merge-train"],
    ["qa", "qa", "working", "Bash", 1, "ah/merge-train"],
    ["so-lane-board", "sub-orchestrator", "working", "Task", 0, "ah/lane-board"],
    ["so-lane-delivery", "sub-orchestrator", "working", "Task", 3, "ah/lane-delivery"],
  ],
  // ah2-225: jobs [lane key | null, owner, kind, label, minutes ago, eta seconds, {slot, slotMin, overdue (minutes), ended}] and running
  // subagents [lane key | null, owner, owner kind, agent id, agent type, minutes ago]. a-rev1 is in the parent's slice and its lane's, as the
  // daemon serves a Sub-Orchestrator's run; it is drawn once, in the lane. "ghost" is on no team; the ended job is not drawn.
  jobs: [
    [null, "qa", "test", "full QA: dotnet test Ah.sln", 9, 900, { slot: "holding", slotMin: 8 }],
    [null, "orchestrator", "slot", "full QA at 3f2a1c9", 2, 900, { slot: "queued" }],
    [null, "ghost", "build", "dotnet build Ah.sln", 1, 300],
    [null, "reviewer", "build", "dotnet build", 20, 300, { ended: 12 }],
    ["board", "board-impl", "smoke", "node --test tests/web/board-kanban.test.mjs", 4, 300],
    ["delivery", "delivery-impl-2", "bg", "npm run bench -- --runs 40 --out /tmp/bench.json", 31, 900, { overdue: 16 }],
  ],
  subagents: [
    [null, "orchestrator", "orchestrator", "a-exp1", "Explore", 3],
    [null, "architect", "member", "a-tg1", "task-gopher:task-gopher", 1],
    [null, "so-lane-board", "member", "a-rev1", "wrangl:reviewer", 5],
    ["board", "orchestrator", "orchestrator", "a-rev1", "wrangl:reviewer", 5],
  ],
  // Each lane's child team: name, role, state, tool, stream branch. The lane is `lane-<key>`, led by `so-lane-<key>`.
  lanes: {
    board: [["board-ux", "ux-engineer", "working", "Edit", "ah/lane-board"], ["board-impl", "implementor", "working", "Bash", "ah/lane-board"], ["board-rev", "reviewer", "idle", null, "ah/lane-board"]],
    delivery: [["delivery-impl-1", "implementor", "working", "Edit", "ah/lane-delivery"], ["delivery-impl-2", "implementor", "blocked", null, "ah/lane-delivery"], ["delivery-rev", "reviewer", "working", "Read", "ah/lane-delivery"]],
  },
  context: {
    architect: ["claude-opus-4-5", 74_100],
    reviewer: ["claude-opus-4-5", 41_800],
    qa: ["claude-haiku-4-5", 19_600],
    "so-lane-board": ["claude-opus-4-5", 52_300],
    "so-lane-delivery": ["claude-opus-4-5", 66_900],
  },
  working1h: { architect: 0.46, reviewer: 0.21, qa: 0.62, "so-lane-board": 0.55, "so-lane-delivery": 0.6 },
  eta: { "lane-board": "large", "lane-delivery": "large", "merge-train": "medium" },
  streams: [
    { stream: "lane-board", owner: "so-lane-board", branch: "ah/lane-board", goal: "Epics and deliverables on the board as an icicle" },
    { stream: "lane-delivery", owner: "so-lane-delivery", branch: "ah/lane-delivery", goal: "Reports delivered exactly once, to the right address" },
    { stream: "merge-train", owner: "qa", branch: "ah/merge-train", goal: "Land rc4 on main, lane by lane" },
  ],
  plan: [
    ["lane-board", "lb1", "Icicle layout for epics", "in_progress", "board-ux", 1],
    ["lane-board", "lb2", "Deliverable rollups", "in_progress", "board-impl", 2],
    ["lane-board", "lb3", "Kind and parent in the board model", "done", null, 3],
    ["lane-board", "lb4", "Keyboard focus through the icicle", "not_started", null, 4],
    ["lane-delivery", "ld1", "Exactly-once report delivery", "in_progress", "delivery-impl-1", 1],
    ["lane-delivery", "ld2", "Survive a session rename mid-send", "blocked", "delivery-impl-2", 2],
    ["lane-delivery", "ld3", "Refuse team_mismatch at send time", "done", null, 3],
    ["lane-delivery", "ld4", "Delivery regression suite", "next", null, 4],
    ["merge-train", "mt1", "Train the board lane onto main", "next", null, 1],
    ["merge-train", "mt2", "Train the delivery lane onto main", "not_started", null, 2],
    ["merge-train", "mt3", "Full suite at the rc4 tip", "in_progress", "qa", 3],
  ],
  checklist: {
    ld2: { role: "implementor", todo: "wait for ld1's dedupe to land", stateMin: 70, updatedMin: 20 },
    mt3: { role: "qa", todo: "rerun the shell suites after the flake fix", stateMin: 40, updatedMin: 6 },
  },
  epics: {
    backlog: {
      b8: { kind: "epic" },
      b9: { kind: "epic" },
      b14: { kind: "deliverable", parent: "b8" },
      b13: { kind: "deliverable", parent: "b8" },
      b20: { kind: "deliverable", parent: "b9" },
      b4: { kind: "deliverable", parent: "b9" },
    },
    checklist: { "lane-board": ["b20", "b20", "b20", "b20"], "lane-delivery": ["b14", "b14", "b13", "b13"], "merge-train": ["b4", "b4", "b4"] },
  },
  exchanges: [
    ["20261031-1102-ln01", "lane-board", "orchestrator", "so-lane-board", 176, 170, null, null, "Open lane: board icicle", "Run the board icicle as a lane: spawn a ux-engineer, an implementor and a reviewer; report at each item.", "Lane open: board-ux, board-impl and board-rev spawned on ah/lane-board."],
    ["20261031-1103-ln02", "lane-delivery", "orchestrator", "so-lane-delivery", 175, 168, null, null, "Open lane: message delivery", "Run message delivery as a lane: two implementors and a reviewer; report at each item.", "Lane open: delivery-impl-1, delivery-impl-2 and delivery-rev spawned on ah/lane-delivery."],
    ["20261031-1105-ln03", "spec-lanes", "orchestrator", "architect", 174, 150, null, null, "Spec: the lanes' shared contract", "Write the one contract both lanes build against: the kind/parent fields and the delivery id.", "Spec at docs/specs/rc4-lanes.md: two fields and one id, with the edge cases."],
    ["20261031-1112-ln04", "lane-board", "so-lane-board", "board-impl", 165, 128, null, "lb3", "Kind and parent in the board model", "Read kind and parent from the slices into the board model.", "DONE @71ac0d3: the model carries kind and parent."],
    ["20261031-1114-ln05", "lane-board", "so-lane-board", "board-ux", 164, null, null, "lb1", "Icicle layout for epics", "Lay out epics, deliverables and items as an icicle on the board, both themes."],
    ["20261031-1116-ln06", "lane-delivery", "so-lane-delivery", "delivery-impl-1", 162, 120, null, "ld3", "Refuse team_mismatch at send time", "Validate the target team in msg send before anything is written.", "DONE @4be19c2: 6 tests, one for a same-named member of another team."],
    ["20261031-1118-ln07", "lane-delivery", "so-lane-delivery", "delivery-impl-2", 160, 70, null, "ld2", "Survive a session rename mid-send", "A rename during send must not lose or double the report.", "BLOCKED: needs ld1's dedupe first; the rename path reuses it."],
    ["20261031-1135-ln08", "lane-board", "board-impl", "board-rev", 125, 112, "20261031-1112-ln04", "lb3", "Review the board model @71ac0d3", "Review 71ac0d3 against the spec.", "VERDICT PASS."],
    ["20261031-1140-ln09", "lane-delivery", "delivery-impl-1", "delivery-rev", 118, 104, "20261031-1116-ln06", "ld3", "Review the team_mismatch guard @4be19c2", "Review 4be19c2; classify findings.", "VERDICT PASS WITH NITS: one helper name."],
    ["20261031-1150-ln10", "lane-board", "so-lane-board", "orchestrator", 110, 108, null, "lb3", "Lane report: lb3 done", "lb3 done and reviewed; lb1 and lb2 in progress.", "Noted; train lb3 with the next batch."],
    ["20261031-1155-ln11", "lane-delivery", "so-lane-delivery", "orchestrator", 102, 100, null, "ld3", "Lane report: ld3 done", "ld3 done and reviewed; ld1 in progress, ld2 blocked on it.", "Noted."],
    ["20261031-1200-ln12", "lane-delivery", "so-lane-delivery", "delivery-impl-1", 100, null, null, "ld1", "Exactly-once report delivery", "A report is delivered once even when the sender retries."],
    ["20261031-1210-ln13", "lane-board", "so-lane-board", "board-impl", 98, null, null, "lb2", "Deliverable rollups", "Roll each deliverable's done/total up from its checklist items."],
    ["20261031-1215-ln14", "merge-train", "orchestrator", "qa", 95, 60, null, "mt3", "Full suite at the rc4 tip", "Run the full suite at the rc4 tip; report failures with whether each is in range.", "FAIL: 1 shell suite, test-ahd-fallback, out of range (flaky on the runner)."],
    ["20261031-1225-ln15", "lane-board", "board-ux", "so-lane-board", 85, 83, "20261031-1114-ln05", "lb1", "Question: icicle depth on small screens", "Below 1024 px, collapse items into their deliverable, or scroll sideways?", "Collapse into the deliverable; scrolling sideways hides the rollup."],
    ["20261031-1230-ln16", "lane-delivery", "delivery-impl-1", "so-lane-delivery", 80, 78, "20261031-1200-ln12", "ld1", "Question: dedupe on request id or content?", "Dedupe on the request id, or on a hash of the report body?", "The request id: the same text can legitimately be sent twice."],
    ["20261031-1240-ln17", "spec-lanes", "so-lane-delivery", "architect", 76, 62, null, "ld1", "Question: is the delivery id stable across rename?", "Does the spec's delivery id survive a session rename?", "Yes: it is the request id, which a rename never touches. Spec amended to say so."],
    ["20261031-1250-ln18", "merge-train", "orchestrator", "reviewer", 64, 50, null, "mt1", "Pre-train review: board lane", "Review the board lane's merged range before it trains.", "VERDICT PASS: ready to train once lb2 lands."],
    ["20261031-1300-ln19", "merge-train", "qa", "orchestrator", 58, 57, "20261031-1215-ln14", "mt3", "Note: flaky suite quarantined", "test-ahd-fallback quarantined; rerunning the shell suites.", "Noted."],
    ["20261031-1310-ln20", "lane-board", "board-ux", "board-rev", 50, 36, "20261031-1114-ln05", "lb1", "Review the icicle tokens", "Review the icicle's colour and type tokens in both themes.", "VERDICT CHANGES REQUIRED: deliverable rows are 3.9:1 in dark; need 4.5:1."],
    ["20261031-1320-ln21", "lane-delivery", "so-lane-delivery", "orchestrator", 45, 44, null, "ld1", "Lane report: ld1 halfway", "ld1 halfway: request-id dedupe in, the retry path left.", "Noted; keep ld2 parked until ld1 lands."],
    ["20261031-1330-ln22", "lane-board", "so-lane-board", "orchestrator", 38, 37, null, "lb1", "Lane report: lb1 in review", "lb1 back from review with a contrast fix; lb2 rolling up.", "Noted."],
    ["20261031-1335-ln23", "merge-train", "orchestrator", "qa", 30, null, null, "mt3", "Rerun the shell suites at the rc4 tip", "Rerun the shell suites at the rc4 tip with the flaky one quarantined."],
    ["20261031-1340-ln24", "lane-delivery", "delivery-impl-1", "delivery-rev", 24, 12, "20261031-1200-ln12", "ld1", "Pre-review: dedupe @c51e0aa", "Look at the dedupe before the retry path goes in.", "Reads clean; test the same-text case."],
    ["20261031-1345-ln25", "spec-lanes", "orchestrator", "architect", 20, null, "20261031-1105-ln03", null, "Spec r2: rollup of a dropped item", "Say how a deliverable's rollup counts a dropped checklist item."],
    ["20261031-1350-ln26", "lane-board", "board-ux", "board-rev", 10, null, "20261031-1310-ln20", "lb1", "Re-review the icicle tokens", "Contrast fixed: every row at 4.5:1 or better in both themes."],
    ["20261031-1352-ln27", "lane-delivery", "so-lane-delivery", "orchestrator", 6, null, null, "ld2", "Question: unpark ld2 now?", "ld1's dedupe is in review. Unpark ld2 now, or after ld1 merges?"],
    ["20261031-1355-ln28", "merge-train", "orchestrator", "so-lane-board", 4, null, "20261031-1250-ln18", "mt1", "Train window at 15:00", "The board lane trains at 15:00. Land lb2 or hold it for the next window."],
  ],
  traffic: [
    ["orchestrator", "so-lane-board", "lane-board"],
    ["so-lane-delivery", "orchestrator", "lane-delivery"],
    ["orchestrator", "qa", "merge-train"],
    ["architect", "orchestrator", "spec-lanes"],
    ["orchestrator", "reviewer", "merge-train"],
    ["so-lane-board", "orchestrator", "lane-board"],
  ],
};

// TEAM with its implementors under the names a roster gives them, and four more of them, all working.
const prefixed = (json) => json.replace(/"implementor-([12])"/g, (_, n) => `"ship-implementor-${n === "1" ? 89 : 96}"`);
const BUSY = JSON.parse(prefixed(JSON.stringify(TEAM)));
// An Orchestrator records no activity through its turns: idle on the record while it leads, as the daemon serves it.
BUSY.leadIdle = true;
BUSY.members = [
  ["architect", "architect", "idle", null, 14, "ah/liveness"],
  ["ship-implementor-89", "implementor", "working", "Bash", 0, "ah/msg-delivery"],
  ["ship-implementor-96", "implementor", "working", "Edit", 0, "ah/board-icicle"],
  ["ship-implementor-106", "implementor", "working", "Edit", 1, "ah/msg-delivery"],
  ["ship-implementor-109", "implementor", "working", "Read", 0, "ah/board-icicle"],
  ["ship-implementor-110", "implementor", "working", "Bash", 2, "ah/msg-delivery"],
  ["ship-implementor-111", "implementor", "working", "Edit", 0, "ah/liveness"],
  ["reviewer", "reviewer", "idle", null, 3, "ah/msg-delivery"],
  ["qa", "qa", "idle", null, 6, "ah/msg-delivery"],
  ["ux-engineer", "ux-engineer", "idle", null, 480, "ah/board-icicle"],
];

export const SCENARIOS = { team: TEAM, busy: BUSY, lanes: LANES };
