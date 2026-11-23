import { html, Component } from "./h.js";

// Report evidence (adr-39, spec artifact-review A8): `ah msg fill --evidence` appends lines of the form
// `- [e1] name · type · size · evidence:<msg id>/e1`; each is drawn from the read-only route GET /ui/v1/evidence/<msg id>/e<k>.
// Images, audio and video inline with their controls, text in a capped block, anything else as a download link. An svg is never
// drawn as an image: it and every other type go out as a link.
const LINE = /^- \[e(\d+)\] (.+) · ([a-z]+\/[\w.+-]+) · (\d+(?:\.\d+)? (?:B|KiB|MiB)) · evidence:([\w-]+)\/e(\d+)$/;
const TEXT_CAP = 64 * 1024;

export const evidenceUrl = (id, k) => `/ui/v1/evidence/${encodeURIComponent(id)}/e${k}`;

// A body's text as alternating prose and evidence items; a body with no evidence line is the one prose block it always was.
export function evidenceParts(text) {
  const parts = [];
  let prose = [];
  const flush = () => {
    const block = prose.join("\n").replace(/^\n+|\s+$/g, "");
    block && parts.push({ prose: block });
    prose = [];
  };
  for (const line of text.split("\n")) {
    const m = LINE.exec(line);
    if (m && m[1] === m[6]) {
      // the generated heading says what the figures already show
      prose.at(-1) === "## evidence" && prose.pop();
      flush();
      parts.push({ k: Number(m[1]), name: m[2], type: m[3], size: m[4], url: evidenceUrl(m[5], m[1]) });
    } else {
      prose.push(line);
    }
  }
  flush();
  return parts.some((p) => p.prose === undefined) ? parts : [{ prose: text }];
}

export const kindOf = (type) => (type === "image/svg+xml" ? "file"
  : type.startsWith("image/") ? "image"
  : type.startsWith("audio/") ? "audio"
  : type.startsWith("video/") ? "video"
  : type.startsWith("text/") || type === "application/json" ? "text"
  : "file");

// The first TEXT_CAP bytes of a file, and whether there was more; the rest is never read.
async function headOf(url, signal) {
  const reply = await fetch(url, { signal });
  if (!reply.ok) {
    throw new Error(String(reply.status));
  }
  const reader = reply.body.getReader();
  const bytes = new Uint8Array(TEXT_CAP);
  let n = 0;
  let cut = false;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    const room = TEXT_CAP - n;
    bytes.set(value.subarray(0, room), n);
    n += Math.min(room, value.length);
    if (value.length > room || n === TEXT_CAP) {
      cut = value.length > room || !(await reader.read()).done;
      break;
    }
  }
  reader.cancel().catch(() => {});
  // stream: true leaves out a character the cap cut in two
  return { text: new TextDecoder().decode(bytes.subarray(0, n), { stream: cut }), cut };
}

class EvidenceText extends Component {
  componentDidMount() {
    this.abort = new AbortController();
    headOf(this.props.url, this.abort.signal).then((got) => this.setState({ got }), () => this.abort.signal.aborted || this.setState({ failed: true }));
  }

  componentWillUnmount() {
    this.abort.abort();
  }

  render({ name }, { got, failed }) {
    return failed
      ? html`<p class="ev-frame ev-note dim">Could not load ${name}.</p>`
      : got
        ? html`<pre class="ev-frame ev-text" tabindex="0" aria-label=${name}>${got.text}</pre>${got.cut ? html`<p class="ev-cut dim">First 64 KiB shown. Open the file for the rest.</p>` : null}`
        : html`<p class="ev-frame ev-note dim" aria-busy="true">Loading…</p>`;
  }
}

function Evidence({ e }) {
  const kind = kindOf(e.type);
  const media = kind === "image" ? html`<div class="ev-frame"><img src=${e.url} alt=${e.name} loading="lazy" decoding="async" /></div>`
    : kind === "audio" ? html`<audio class="ev-audio" controls preload="metadata" src=${e.url} aria-label=${e.name}></audio>`
    : kind === "video" ? html`<div class="ev-frame"><video controls preload="metadata" src=${e.url} aria-label=${e.name}></video></div>`
    : kind === "text" ? html`<${EvidenceText} url=${e.url} name=${e.name} />`
    : null;
  return html`<figure class="ev" data-kind=${kind}>
    <figcaption><span class="ev-k">e${e.k}</span> <a href=${e.url} target="_blank" rel="noopener" download=${kind === "file" ? e.name : undefined}>${e.name}</a> <span class="dim">${e.type} · ${e.size}</span></figcaption>
    ${media}
  </figure>`;
}

// A report body: its prose as before, each evidence line in its place drawn as the file it names.
export function ReportBody({ text }) {
  return evidenceParts(text).map((p) => (p.prose !== undefined ? html`<p class="prose">${p.prose}</p>` : html`<${Evidence} key=${p.url} e=${p} />`));
}
