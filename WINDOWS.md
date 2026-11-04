# Windows port brief (for the Windows Claude Code session)

The Windows split already exists in the private `2-X/claude-restyler` repo
(`windows/`: `apply-blackmode.ps1`, `pin-profile.js`, `theme.conf`, a scheduled
task, the claude:// link router). It handles a fixed Personal/Work pair. It is
missing what the Mac got in MultiClaude on 2026-11-04:

## 1. Any number of copies, managed from a tray icon ("universal taskbar icon")

The Mac version is a menubar app over a JSON CLI (`multiclaude --json …`). The
Windows equivalent is a notification-area (tray) app with the same menu:

- One submenu per copy: Open/Quit, Change Color, Copy MCP Servers To, Copy/Move
  Routines To, Hand Gateway Setup To, Show Profile, Remove.
- New Instance… (name, icon color, starburst color, UI tint toggle, copy MCP from).
- Upgrade All, which shows the latest version from the update feed and stages
  every copy; a running copy switches over when it closes, then relaunches.
- "Runs when these apps quit", the deferred queue, each item with Quit-now and
  Cancel.
- The window cycler (on Mac: ⌘\`, ⇧⌘\`, ⌃\`, active only while a Claude copy is
  focused). It cycles all copies' windows as if they were one app, and launches
  a copy that has no window.

Registry: `%USERPROFILE%\.config\multiclaude\instances.json`, same schema as
the Mac (`name, bundle, bundle_id, profile, color, glyph, tint`). On Windows,
`bundle` is the copy's folder and `bundle_id` is the AppUserModelID
`Claude.Split.<Name>`. `theme.conf` becomes generated output, as it is on the
Mac, so `apply-blackmode.ps1` keeps working.

Per-copy icons: `pin-profile.js` already sets the AUMID and window icon, and
`apply-blackmode.ps1` renders the `.ico`. New copies need a Start menu shortcut
carrying the same AUMID. On a color change, re-render the `.ico` and refresh
the shortcut, so the pinned taskbar button picks up the new color.

## 2. Bedrock / gateway ("3p") mode per copy

On the Mac, 3p state lives in `<userData>-3p`, so each copy gets its own. On
**Windows the app ignores the copy's userData** for this. From 2.31226.1's
`index.pre.js` (function that builds the 3p dir):

```js
if (process.env.CLAUDE_USER_DATA_DIR) return app.getPath("userData");
if (process.platform === "win32" && process.env.LOCALAPPDATA) return path.join(process.env.LOCALAPPDATA, "Claude-3p");
let e = app.getPath("userData"); return e.endsWith("-3p") ? e : e + "-3p";
```

So every copy shares `%LOCALAPPDATA%\Claude-3p`. Applying the Bedrock config
in one copy flips **all** copies into gateway mode, and they lose their
claude.ai sign-in. There is also a legacy migration from `%APPDATA%\Claude-3p`.

To do:
- Check this against the asar the PC actually runs (3.x/4.x). Search the main
  entry for `"Claude-3p"`, `deploymentMode`, `configLibrary`.
- Fix it in `pin-profile.js`, which runs before Claude's code. Either set
  `process.env.CLAUDE_USER_DATA_DIR` so the first branch returns the copy's own
  dir (the README notes packaged builds delete that variable, so set it again
  after the deletion point or patch the call), or pin the 3p dir per copy
  (`%APPDATA%\<Copy>-3p`).
- Pin a copy to claude.ai with `{"deploymentMode":"1p"}` in its 3p
  `claude_desktop_config.json`. The app reads it as `deploymentMode` = `"1p"|"3p"`.
- The handoff (`move-3p SRC DST`) is the same as on the Mac: once both copies
  are closed and nothing else is using the source's 3p dir, rename it into the
  target's 3p slot, then write the `1p` pin into the source.

## 3. Hook v9

The Mac hook is now `CLAUDE-BLACKMODE-HOOK v9`. Its move worker reads sessions
from `app.getPath("userData")` at call time, so it works for any copy and in 3p
mode. The Windows `v8-win` hook already reads the copy's userData; make sure it
also follows the 3p redirect, bump it to `v9-win`, and keep `tests/hook-parity.js`
green.

## 4. Things that carry over unchanged

- Routines: `<userData>\claude-code-sessions\<account>\<org>\scheduled-tasks.json`
  (`scheduledTasks[]`); prompt bodies in `~\.claude\scheduled-tasks\<id>\SKILL.md`
  are shared. Copy: drop `lastRunAt`, `lastScheduledFor`, `notifySessionId`,
  and set `enabled:false` at the source on a move (never delete).
- MCP: merge `mcpServers` in `claude_desktop_config.json`, and copy
  `Claude Extensions`, `Claude Extensions Settings`,
  `extensions-installations.json` and `mcp-user-tool-toggles.json`.
- Update feed: `https://api.anthropic.com/api/desktop/<platform>/<arch>/squirrel/update?device_id=<uuid>&version=<cur>&os_version=<os>`.
  On Windows, keep the existing robocopy-from-the-installed-app approach unless
  the feed has a win32 zip.
- Deferred ops: never touch a running copy. Queue the change and run it once
  that copy's `claude.exe` is gone.
