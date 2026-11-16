export class Ended extends Error {}

// What the user reads when the session is over, by the reason the bridge saw: a 503 (the daemon is not serving), or anything else.
// Ended's message carries the reason.
export const endedText = (why) => (why === "503" ? "The daemon isn't running." : "The UI closed. Run ahd ui start to open it again.");

const MOCK_KEY = "ah.ui.mock";

function storedMock() {
  try {
    return sessionStorage.getItem(MOCK_KEY) === "1";
  } catch {
    return false;
  }
}

function keepMock(on) {
  try {
    sessionStorage.setItem(MOCK_KEY, on ? "1" : "0");
  } catch {
    // the page works for this load without it
  }
}

const FIRST_LOAD_MS = 15000;

// Runs `run(signal)` and fails, naming `label`, when it has not finished within `ms`, so a request nobody answers never leaves the page
// waiting for ever. A stop through `signal` is passed on as it is.
async function within(label, ms, signal, run) {
  const own = new AbortController();
  const timer = setTimeout(() => own.abort(), ms);
  const relay = () => own.abort();
  signal?.addEventListener("abort", relay);
  try {
    return await run(own.signal);
  } catch (e) {
    if (own.signal.aborted && !signal?.aborted) {
      throw new Error(`${label}: no answer after ${ms / 1000} s`);
    }
    throw e;
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", relay);
  }
}


// Answers an open question (POST /ui/v1/answer).
// Resolves to { status, body } for every reply the server gives, the 409 refusals included; fails only when none came.
export async function postAnswer(answer, ms = FIRST_LOAD_MS) {
  return within("answer", ms, undefined, async (signal) => {
    const reply = await fetch("/ui/v1/answer", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(answer),
      signal,
    });
    return { status: reply.status, body: await reply.json().catch(() => null) };
  });
}

// Whether the Orchestrator has seen an answer's event (GET /ui/v1/answer-status): { status, body } as postAnswer.
export async function getAnswerStatus({ dir, team, event }, ms = FIRST_LOAD_MS) {
  const q = new URLSearchParams({ dir, team, event });
  return within("answer-status", ms, undefined, async (signal) => {
    const reply = await fetch(`/ui/v1/answer-status?${q}`, { signal });
    return { status: reply.status, body: await reply.json().catch(() => null) };
  });
}

// Previews, closes or reopens a team (POST /ui/v1/team-close, ah-ui-focus §6). Replies as postAnswer does; ending sessions can take a while.
export async function postTeamClose(body, ms = 90_000) {
  return within("team-close", ms, undefined, async (signal) => {
    const reply = await fetch("/ui/v1/team-close", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return { status: reply.status, body: await reply.json().catch(() => null) };
  });
}

// The rules of engagement (GET and POST /ui/v1/rules, rules-of-engagement §6). A refusal is an answer: the reply carries its status and the server's words.
export async function getRules(team, ms = 15_000) {
  return within("rules", ms, undefined, async (signal) => {
    const reply = await fetch(`/ui/v1/rules?${new URLSearchParams({ team })}`, { signal });
    return { status: reply.status, body: await reply.json().catch(() => null) };
  });
}

export async function postRule(body, ms = 15_000) {
  return within("rules", ms, undefined, async (signal) => {
    const reply = await fetch("/ui/v1/rules", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return { status: reply.status, body: await reply.json().catch(() => null) };
  });
}

// The daemon serves lists, actions and the read model at routes of their own (GET /ui/v1/list/<id>, /ui/v1/actions[/<id>],
// /ui/v1/read-model); every other path is a view read through ?path=.
export function urlOf(path, params = {}) {
  const direct = /^\/(list|actions)(\/|$)/.test(path) || path === "/read-model";
  if (!direct) {
    return `/ui/v1/view?${new URLSearchParams({ path, ...params })}`;
  }
  const segments = path.split("/").map((s) => encodeURIComponent(s).replace(/%3A/g, ":").replace(/%40/g, "@"));
  const query = new URLSearchParams(params).toString();
  return `/ui/v1${segments.join("/")}${query ? `?${query}` : ""}`;
}

// Returns get(path, params, signal): the served view document at `path`, with `params` as further query
// parameters (the events cursor). A 401 or 503 rejects with an Ended carrying that `status`: the first read shows it, and the poll
// retries through it, since a daemon restart answers both on the way back. Each manifest read fails
// after `timeoutMs` with no answer; the events long-poll does not.
export async function connect({ timeoutMs = FIRST_LOAD_MS } = {}) {
  let mock = storedMock();
  const read = async (url, label, signal) => {
    const reply = await fetch(url, { signal });
    mock = reply.headers.get("X-Ah-Mock") === "1";
    keepMock(mock);
    if (reply.status === 401 || reply.status === 503) {
      throw Object.assign(new Ended(String(reply.status)), { status: reply.status });
    }
    if (!reply.ok) {
      throw Object.assign(new Error(`view ${label}: ${reply.status}`), { status: reply.status });
    }
    return reply.json();
  };
  const get = (path, params = {}, signal = undefined) => read(urlOf(path, params), path, signal);
  get.manifest = (name, signal = undefined) =>
    within(`view ${name}`, timeoutMs, signal, (stop) => read(`/ui/v1/view/${encodeURIComponent(name)}`, name, stop));
  // True once the server has said it is the mock source (X-Ah-Mock). The daemon never sends that header.
  Object.defineProperty(get, "mock", { get: () => mock });
  return get;
}
