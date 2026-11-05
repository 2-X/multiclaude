// Checks pin-profile.js against a fake Electron: profile pin, the 3p folder
// redirect, the AppUserModelID and the quit request.  node windows\tests\pin-test.js
const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const Module = require("module");

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "mc-pin-"));
const home = path.join(tmp, "home");
const appData = path.join(tmp, "Roaming");
const localAppData = path.join(tmp, "Local");
[home, appData, localAppData].forEach((d) => fs.mkdirSync(d, { recursive: true }));
process.env.APPDATA = appData;
process.env.LOCALAPPDATA = localAppData;
os.homedir = () => home;
Object.defineProperty(process, "execPath", { value: path.join(tmp, "Applications", "Claude Test Box", "claude.exe") });

const calls = { quit: 0, ids: [], paths: {}, switches: {} };
let readyCb;
const fakeApp = {
  commandLine: { hasSwitch: (k) => k in calls.switches, getSwitchValue: (k) => calls.switches[k], appendSwitch: (k, v) => { calls.switches[k] = v; } },
  getPath: (k) => (k === "appData" ? appData : calls.paths[k]),
  setPath: (k, v) => { calls.paths[k] = v; },
  setAppUserModelId: (id) => calls.ids.push(id),
  on: () => {},
  whenReady: () => ({ then: (cb) => { readyCb = cb; } }),
  quit: () => { calls.quit++; },
};
const electron = { app: fakeApp, shell: { openExternal: async () => true }, BrowserWindow: { getAllWindows: () => [] } };
const load = Module._load;
Module._load = function (req, ...rest) { return req === "electron" ? electron : load.call(this, req, ...rest); };

eval(fs.readFileSync(path.join(__dirname, "..", "pin-profile.js"), "utf8"));

const own = path.join(appData, "Claude-Test-Box");
assert.strictEqual(calls.paths.userData, own, "userData pinned to %APPDATA%\\Claude-Test-Box");
assert.strictEqual(calls.switches["user-data-dir"], own);
assert.strictEqual(calls.ids[0], "Claude.Split.Claude-Test-Box");
fakeApp.setAppUserModelId("Claude.Claude");
assert.strictEqual(calls.ids[calls.ids.length - 1], "Claude.Split.Claude-Test-Box", "Claude can't replace the AUMID");

// Claude's own expressions for its 3p folder (index.chunk ju()/qWe() in 4.29).
assert.strictEqual(path.join(process.env.LOCALAPPDATA, "Claude-3p"), own + "-3p");
assert.strictEqual(path.join(process.env.APPDATA, "Claude-3p"), own + "-3p");
assert.strictEqual(path.win32.join(localAppData + "\\", "claude-3p"), own + "-3p", "case and trailing slash");
assert.strictEqual(path.join(path.join(own + "-3p"), "configLibrary"), path.resolve(own + "-3p", "configLibrary"));
assert.strictEqual(path.join("C:\\elsewhere", "Claude-3p"), "C:\\elsewhere\\Claude-3p", "other roots untouched");
assert.strictEqual(path.join(localAppData, "Claude-3p", "x"), path.resolve(localAppData, "Claude-3p", "x"), "3+ parts untouched");
assert.strictEqual(path.join(localAppData, "Claude"), path.resolve(localAppData, "Claude"));

// Quit request: stale ones are ignored, fresh ones quit.
const qdir = path.join(home, ".config", "multiclaude", "quit");
fs.mkdirSync(qdir, { recursive: true });
const req = path.join(qdir, "claude-test-box.req");
const realSetInterval = global.setInterval;
let tick;
global.setInterval = (fn) => { tick = fn; return { unref() {} }; };
global.clearInterval = () => {};
readyCb();
global.setInterval = realSetInterval;
fs.writeFileSync(req, "old");
const old = new Date(Date.now() - 3600e3);
fs.utimesSync(req, old, old);
tick();
assert.strictEqual(calls.quit, 0, "stale request ignored");
assert.ok(!fs.existsSync(req), "stale request removed");
fs.writeFileSync(req, "now");
tick();
assert.strictEqual(calls.quit, 1, "fresh request quits");

fs.rmSync(tmp, { recursive: true, force: true });
console.log("pin-profile.js: all checks passed");
