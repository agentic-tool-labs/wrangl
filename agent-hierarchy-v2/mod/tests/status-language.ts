// A copy of tests/fixtures/status-language.json; the native suite checks the two are the same. Edit both together.
export const statusLanguage = {
  "states": {
    "stuck": { "glyph": "▲", "word": "stuck", "tone": "bad", "rank": 0 },
    "late": { "glyph": "▲", "word": "late", "tone": "bad", "rank": 1 },
    "needsYou": { "glyph": "■", "word": "needs you", "tone": "warn", "rank": 2 },
    "dueSoon": { "glyph": "◔", "word": "due soon", "tone": "warn", "rank": 3 },
    "working": { "glyph": "●", "word": "working", "tone": "work", "rank": 4 },
    "waitingOrch": { "glyph": "◐", "word": "waiting for the orchestrator", "tone": "idle", "rank": 5 },
    "idle": { "glyph": "○", "word": "idle", "tone": "idle", "rank": 6 },
    "held": { "glyph": "‖", "word": "Held", "tone": "idle", "rank": 6 },
    "done": { "glyph": "✓", "word": "done", "tone": "ok", "rank": 7 },
    "ended": { "glyph": "×", "word": "ended", "tone": "idle", "rank": 8 },
    "unknown": { "glyph": "?", "word": "unknown", "tone": "idle", "rank": 9 }
  },
  "from": {
    "working": "working",
    "idle": "idle",
    "blocked": "needsYou",
    "needs_you": "needsYou",
    "ended": "ended",
    "waiting_user": "needsYou",
    "waiting_orch": "waitingOrch",
    "stalled": "stuck",
    "overdue": "late",
    "due-soon": "dueSoon",
    "reported": "done",
    "gone": "ended",
    "orphaned": "ended",
    "stale": "ended",
    "expired": "ended"
  },
  "liveness": {
    "live": "",
    "unknown": "unknown",
    "not_seen": "not seen",
    "proven_dead": "gone"
  }
} as const
