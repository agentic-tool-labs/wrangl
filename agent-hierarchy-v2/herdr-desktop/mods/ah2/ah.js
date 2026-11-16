// The herdr side of target-model.js: this copy's target and runner, from the "ah runs on" setting, the platform and herdr's machines.
// Every exec that reaches ah, and every read of a file in ah's home, takes its runner from here.

import { exec, readTextFile, machines, window, shared } from "herdr";
import { targetOf, runnerOf } from "./target-model.js";
import { SETTINGS_KEY, settingsOf } from "./settings-model.js";

// The setting under which an auto WSL target found no wsl.exe: auto stays unresolved until the setting changes.
let missing = null;
const platform = () => window()?.platform ?? null;
const setting = () => settingsOf(shared(SETTINGS_KEY)).ahOn;

export const target = () => targetOf({ platform: platform(), machines: machines(), setting: setting(), wsl: missing !== setting() });
export const ah = () => {
  const s = setting();
  return runnerOf(exec, readTextFile, target(), { platform: platform(), onMissingWsl: () => { missing = s; } });
};
