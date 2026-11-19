import { html } from "./h.js";
import { Live } from "./live-component.js";
import { DUTY, DUTY_TITLE, NO_CONTEXT, briefsInFlight, membersOf, needsCount, needsState, needsText, stateWord, teamSummary, partyIdOf, partyName } from "./select.js";
import { hoverProps } from "./hover.js";
import { memberLink } from "./member-summary.js";
import { memberHref } from "./deeplink.js";
import { flattenTree } from "./treeview.js";
import { ageOf, ageText } from "./store.js";
import { Age } from "./ticker.js";
import { KEY_TABLE } from "./keys.js";
import { graphSources, jobsUnder, runText, underTip } from "./flow-model.js";
import { UNDER_GLYPH } from "./graph.js";

// The member-step keys as the help table names them (B6: the hint was "j / k", which nothing binds).
const MEMBER_KEYS = KEY_TABLE.find((k) => k.hint === "member").label;

// The Overview: members grouped by what they are doing, four team numbers, the briefs in flight and the hierarchy as text.
// A number the daemon does not publish (the last push) shows "—".

const GLYPH = { working: "◐", blocked: "▲", idle: "○", gone: "○", unknown: "?" };

// Not knowing is not idle, and a member proven dead is not idle either.
export const memberGroups = (people) => ({
  working: people.filter((m) => m.state === "working"),
  blocked: people.filter((m) => m.state === "blocked"),
  running: people.filter((m) => m.running),
  idle: people.filter((m) => m.state === "idle" && !m.running),
  unknown: people.filter((m) => m.state === "unknown" && !m.running),
  gone: people.filter((m) => m.state === "gone"),
});

const ageOfMs = (at) => (now) => ageText(ageOf({ as_of: at }, {}, now)).replace(" ago", "");
// A brief row's Status word, and which In-flight columns carry nothing (L4): Branch when no row has one, Status when every row
// reads the same word. No rows hides both.
const briefStatus = (b) => (b.state === "working" ? "working" : "open");
export const hiddenColumns = (briefs) => ({ branch: briefs.every((b) => !b.branch), status: new Set(briefs.map(briefStatus)).size <= 1 });
const timeText = (ms) => (ms > 0 ? html`<${Age} text=${ageOfMs(ms)} />` : "—");

// The hierarchy rows as text lines: indentation glyphs by depth, then the label and its detail.
export function hierarchyLines(rows) {
  return rows.map((r) => ({ key: r.key, branch: r.depth === 0 ? "" : `${"│  ".repeat(r.depth - 1)}├─ `, kind: r.kind, state: r.state, label: r.label, detail: r.kind === "brief" ? r.detail : r.detail ?? "" }));
}

// A team's goal on its own page (ah2-53): "Goal" and the text, then "Done when" and its text when it has one. A lane's inherited goal
// says whose it is. Nothing at all when the team has no goal. The text is inert (select.js goalOf) and drawn as text, never as markup.
// A plain call, not a component, so it draws only when its view does.
export const teamGoal = (goal, name, cls) =>
  goal
    ? html`<section class=${`team-goal ${cls}`} aria-label="Team goal"><dl>
        <div><dt>Goal${goal.team && goal.team !== name ? html` <span class="goal-team">from team ${goal.team}</span>` : null}</dt><dd>${goal.text}</dd></div>
        ${goal.doneWhen ? html`<div><dt>Done when</dt><dd>${goal.doneWhen}</dd></div>` : null}
      </dl></section>`
    : null;

export class OverviewView extends Live {
  select(snap, { teamId }) {
    const team = snap.slices.get(teamId);
    if (!team) {
      this.model = null;
      return { key: "" };
    }
    const t = team.body;
    const sum = teamSummary(snap.slices, teamId);
    const everyone = membersOf(snap.slices, team);
    const people = everyone.filter((m) => m.state !== "gone");
    const byId = new Map(people.map((m) => [m.id, m]));
    const list = briefsInFlight(snap.slices, team);
    const unattended = (snap.slices.get(`list:open:team:${t.dir_id}/${t.name}`)?.body.open ?? []).filter((o) => o.unattended_at).length;
    const stale = snap.slices.get(`team-summary:${t.dir_id}/${t.name}`)?.body.stale_ids?.length ?? 0;
    const briefs = list
      .map((o) => snap.slices.get(o.id))
      .filter(Boolean)
      .map((x) => {
        const to = byId.get(x.body.to_member ?? "");
        const branch = to ? snap.slices.get(`worktree:${t.dir_id}/${t.name}/${to.name}`)?.body.stream ?? null : null;
        return { id: x.id, slug: x.body.title || x.body.slug, from: partyName(x.body, "from", snap.slices), to: partyName(x.body, "to", snap.slices), fromId: partyIdOf(x.body, "from"), toId: partyIdOf(x.body, "to"), state: to?.state ?? "idle", branch, at: Date.parse(x.body.created ?? "") || 0 };
      })
      .sort((a, b) => a.at - b.at || (a.id < b.id ? -1 : 1));
    const decisions = (sum?.items ?? []).filter((i) => i.kind === "Decision");
    this.model = {
      ...memberGroups(people),
      goneCount: everyone.length - people.length,
      briefs,
      stale,
      unattended,
      needs: sum ? needsCount(snap.slices, teamId) : 0,
      needsPending: sum ? needsState(snap.slices, teamId).pending : false,
      needsAt: decisions.length ? Math.min(...decisions.map((d) => d.at).filter((a) => a > 0)) : 0,
      decisions: Number.isFinite(t.pipeline?.decisions_count) ? t.pipeline.decisions_count : null,
      decidedAt: Date.parse(t.pipeline?.last_decision_at ?? "") || 0,
      oldest: briefs.find((b) => b.at > 0)?.at ?? 0,
      lines: hierarchyLines(flattenTree(snap.slices, team)),
      name: t.name,
      goal: sum?.goal ?? null,
    };
    // ah2-225 B1: each member's jobs under its row, and any whose owner is no one here under the Members header
    this.model.under = jobsUnder(graphSources(snap.slices, team, everyone.map((p) => p.id), [], "jobs"));
    const m = this.model;
    return { under: JSON.stringify([...m.under]), key: JSON.stringify([[...m.working, ...m.blocked, ...m.running, ...m.idle, ...m.unknown, ...m.gone].map((p) => [p.id, p.name, p.role, p.state, p.slug, p.since, p.ctx.text, p.ctx.over, p.facts, p.duty]), m.briefs, m.stale, m.unattended, m.needs, m.needsPending, m.needsAt, m.decisions, m.decidedAt, m.lines, m.goal]), selected: snap.ui.selected ?? null };
  }

  pick = (id) => this.props.store.setUi({ selected: this.props.store.state.ui.selected === id ? null : id });

  // A member's jobs, as rows after its row (or under the Members header): no target, it opens no pane; the full text is the title.
  unders(list, parent) {
    return (list ?? []).map((x) => html`<li key=${x.key} class="ov-under" title=${underTip(x, parent, Date.now())}><span class="g" aria-hidden="true">${UNDER_GLYPH.job}</span><span class="who">${x.label}</span>${x.overdue ? html`<span class="eta overdue"><span class="eta-glyph" aria-hidden="true">⚠</span>overdue</span>` : html`<span></span>`}<span class="since"><${Age} text=${(now) => runText(x, now)} /></span></li>`);
  }

  member(m, selected) {
    // The row selects the member (its summary opens in the rail); the name inside it is the link to the member's page.
    return html`<li key=${m.id} class=${`ov-member st-${m.running ? "working" : m.state}${selected === m.id ? " selected" : ""}`} data-role=${m.role || undefined} data-state=${m.state} ...${hoverProps(this.props.store, m.id, "strip")}><button class="ov-hit" aria-pressed=${selected === m.id} aria-label=${`Select ${m.title}`} onClick=${() => this.pick(m.id)}></button><span class="ov-row"><span class="g" aria-hidden="true">${m.running ? GLYPH.working : GLYPH[m.state] ?? "○"}</span><span class="who"><a class="mlink" href=${memberHref(m.id)} title=${m.title !== m.label ? m.title : undefined}>${m.label}</a>${DUTY_TITLE[m.duty] ? html`<span class="duty-badge">${m.duty === "on" ? DUTY.on : DUTY_TITLE.away}</span>` : null}${m.slug ? html`<small>${m.slug}</small>` : null}${m.facts ? html`<small class="ov-facts">${m.facts}</small>` : null}</span><span class=${m.ctx.over ? "ctx over" : "ctx"} title=${m.ctx.text === NO_CONTEXT.text ? undefined : m.ctx.title}>${m.ctx.text === NO_CONTEXT.text ? "" : m.ctx.text}</span><span class="since" title=${m.since > 0 ? `${stateWord(m.state)} for this long` : undefined}>${m.since > 0 ? timeText(m.since) : ""}</span></span></li>${this.unders(this.model.under.get(m.id), m.label)}`;
  }

  draw(_, { selected }) {
    const m = this.model;
    if (!m) {
      return html`<p class="dim skeleton">Loading…</p>`;
    }
    const hide = hiddenColumns(m.briefs);
    return html`<section class="overview" aria-label="Overview">
      <nav class="ov-members" aria-label="Members">
        <header><b>Members</b><span class="dim">${m.working.length + m.blocked.length + m.running.length + m.idle.length + m.unknown.length + m.gone.length}${m.goneCount > 0 ? ` · ${m.goneCount} gone` : ""}</span><span class="grow"></span><span class="dim keys">${MEMBER_KEYS}</span></header>
        <div class="ov-scroll">
          ${m.under.has("") ? html`<ul aria-label="Jobs of no member">${this.unders(m.under.get(""), null)}</ul>` : null}
          <h4>Working · ${m.working.length}</h4>
          <ul>${m.working.map((p) => this.member(p, selected))}</ul>
          ${m.blocked.length > 0 ? html`<h4>Blocked · ${m.blocked.length}</h4><ul>${m.blocked.map((p) => this.member(p, selected))}</ul>` : null}
          ${m.running.length > 0 ? html`<h4>Running · ${m.running.length}</h4><ul>${m.running.map((p) => this.member(p, selected))}</ul>` : null}
          <h4>Idle · ${m.idle.length}</h4>
          <ul>${m.idle.map((p) => this.member(p, selected))}</ul>
          ${m.unknown.length > 0 ? html`<h4>No signal · ${m.unknown.length}</h4><ul>${m.unknown.map((p) => this.member(p, selected))}</ul>` : null}
          ${m.gone.length > 0 ? html`<h4>Gone · ${m.gone.length}</h4><ul>${m.gone.map((p) => this.member(p, selected))}</ul>` : null}
        </div>
      </nav>
      <div class="ov-centre">
        ${teamGoal(m.goal, m.name, "ov-goal")}
        <div class="ov-stats">
          <div class="stat attn"><h4>Needs you</h4><b>${needsText(m.needs, m.needsPending)}</b><span>${m.needs > 0 ? html`waiting on you${m.needsAt > 0 ? html` · ${timeText(m.needsAt)}` : ""}` : m.needsPending ? "not loaded yet" : "nothing waiting"}</span></div>
          <div class="stat"><h4>In flight</h4><b>${m.briefs.length}</b><span>${m.briefs.length > 0 ? (m.oldest > 0 ? html`oldest ${timeText(m.oldest)}` : "open") : "none open"}${m.stale > 0 ? ` · ${m.stale} stale` : ""}${m.unattended > 0 ? ` · ${m.unattended} unattended` : ""}</span></div>
          ${m.decisions === null ? null : html`<div class="stat"><h4>Decisions</h4><b>${m.decisions}</b><span>${m.decidedAt > 0 ? html`this run · last ${timeText(m.decidedAt)}` : "this run · none yet"}</span></div>`}
        </div>
        <header class="ov-h"><b>In flight</b><span class="dim">${m.briefs.length}</span></header>
        <div class=${`ov-table${hide.status ? " no-status" : ""}${hide.branch ? " no-branch" : ""}`} role="table" aria-label="In flight">
          <div class="row head" role="row">${hide.status ? null : html`<span>Status</span>`}<span>Item</span><span>From → to</span>${hide.branch ? null : html`<span>Branch</span>`}<span>Age</span></div>
          ${m.briefs.length === 0 ? html`<p class="dim empty">No briefs are open. A brief appears here when a member sends one.</p>` : m.briefs.map((b) => html`<div key=${b.id} class=${`row st-${b.state}`} role="row">${hide.status ? null : html`<span class="status">${GLYPH[b.state] ?? "○"} ${briefStatus(b)}</span>`}<span title=${b.slug}><b>${b.slug}</b></span><span title=${`${b.from} → ${b.to}`}>${memberLink(b.fromId, b.from)} → ${memberLink(b.toId, b.to)}</span>${hide.branch ? null : html`<span class="mono">${b.branch ?? "—"}</span>`}<span class="dim">${timeText(b.at)}</span></div>`)}
        </div>
        <header class="ov-h"><b>Hierarchy</b><span class="dim">who is briefing whom</span></header>
        <div class="ov-tree mono" role="tree" aria-label="Hierarchy">${m.lines.map((l) => html`<div key=${l.key} class=${`ln k-${l.kind}${l.state ? ` st-${l.state}` : ""}`}>${l.branch}${l.label}${l.detail ? html` <span class="dim">· ${l.detail}</span>` : null}</div>`)}</div>
      </div>
    </section>`;
  }
}
