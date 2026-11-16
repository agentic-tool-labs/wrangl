// The Herdr runtime has no Intl (herdr-app check: "Intl is not defined"), and graph-world.js builds an Intl.DateTimeFormat when
// it loads. graph-model.js imports this first, so the copy loads unchanged. ponytail: only the 24 h "HH:MM" format graph-world
// asks for; the graph view never shows it. Widen it if a view ever formats dates another way.
const pad = (n) => String(n).padStart(2, "0");
globalThis.Intl ??= { DateTimeFormat: class { format(d) { return `${pad(d.getHours())}:${pad(d.getMinutes())}`; } } };
