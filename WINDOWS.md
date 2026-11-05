# MultiClaude for Windows

Run several copies of the Claude desktop app side by side on Windows: each
with its own sign-in, chats, settings, icon color and Bedrock/gateway setup,
managed from a tray icon. Same engine commands, `--json` replies and registry
as the Mac version. Built and tested 2026-11-05 on Windows 11 against Claude
4.29.0 (Microsoft Store).

> Unofficial. MultiClaude copies Claude's app folder into
> `~\Applications\Claude <Name>` and patches the copy; the installed Claude is
> never modified. The copies can't update themselves. MultiClaude rebuilds
> them when Claude on this PC updates, and **Upgrade All** gets newer builds
> from Anthropic's update feed.

## Install

**From the download:** get `MultiClaude-Windows-<version>.zip` from the
[Windows release](https://github.com/2-X/multiclaude/releases), extract it and
double-click **Install MultiClaude** (its `READ ME FIRST.txt` walks through it).
The exe isn't signed, so SmartScreen may say "Windows protected your PC": More
info, Run anyway.

**From source:** double-click `windows\install.cmd` (or `powershell -File
windows\install.ps1`). It compiles the app with the C# compiler that ships
with Windows (no SDK).

Either way it installs `MultiClaude.exe` (tray) and `multiclaude.com` (CLI) to
`%LOCALAPPDATA%\Programs\MultiClaude`, adds a Start menu entry, puts the CLI on
your PATH, turns on launch at sign-in, registers the claude:// link router,
and starts the tray. `-NoPath`, `-NoLogin`, `-NoRouter` and `-NoStart` skip
those steps. Run it again to update; `uninstall.cmd` removes the program and
keeps your copies. `windows\make-release.ps1` builds the zip.

**One manual step:** Google sign-in and connector OAuth come back as a
`claude://` link, which Windows gives to the regular Claude app. MultiClaude
sends it to the copy that opened the browser (within 15 minutes), but Windows
only lets you pick the default: the installer opens Settings > Default apps,
where you set CLAUDE to MultiClaude. (claude-restyler's Claude Router does the
same job; if it's your default already, leave it.)

Requirements: Windows 10/11 and Claude installed (Microsoft Store or the
claude.com installer). Without either, **Upgrade All** downloads Claude from
the update feed.

## Use

The tray icon (stacked cards in your copies' colors) has the Mac menu:

- One submenu per copy: **Open/Show**, **Quit**, **Change Color…**, **Copy MCP
  Servers To**, **Copy/Move Routines To**, **Hand … Setup To** (copies running
  a gateway config), **Show Profile in Explorer**, **Remove Instance…** (the
  app folder goes to the Recycle Bin, the profile stays).
- **New Instance…**: name, icon and starburst color, UI tint, copy MCP from.
  You get `~\Applications\Claude <Name>`, profile `%APPDATA%\Claude-<Name>`, a
  Start menu entry and its own taskbar button (pin it from the Start menu).
- **Upgrade All to …** / **Check for Updates**.
- **Runs when these apps quit**: queued changes, each with Quit-now and Cancel.
- **Window Cycler (Alt+\`)**: while a Claude window is in front, Alt+\` and
  Alt+Shift+\` cycle every copy's windows (and the stock app's) as if they were
  one app. A copy with no window is opened first. Elsewhere Alt+\` keeps its
  normal meaning.
- **Launch at Login**, **Open Log**, **Quit MultiClaude**.

Closing a copy's window only hides it to its tray icon. **Quit** quits it for
real, so a queued change can run.

## Command line

```bash
multiclaude list
multiclaude create Bedrock --color '#3B2A6B' [--glyph '#FEFCFB'] [--no-tint] [--source Work] [--copy-mcp-from Work] [--launch]
multiclaude recolor Work --color '#15433D'
multiclaude upgrade --check | upgrade [names…] | refresh
multiclaude copy-mcp Work Bedrock | copy-routines Work Bedrock [--move] | move-3p Work Bedrock --launch
multiclaude pending [--cancel ID] | quit NAME [--force] | launch NAME | remove NAME | info NAME | rollback NAME
multiclaude login on|off | router on|off | version
```

State lives in `~\.config\multiclaude\` (`instances.json`, `pending.json`,
`multiclaude.log`, `pin.log`, `link-router.log`, `icons\`); feed downloads in
`%LOCALAPPDATA%\multiclaude\`.

## What the Mac brief asked for, and where it stands

1. **Any number of copies from a tray icon.** Done: tray menu, New Instance,
   recolor (icon updates live in a running copy, pinned taskbar buttons are
   repointed), Upgrade All, the deferred queue with Quit-now/Cancel, and the
   window cycler. Registry schema as on the Mac; `bundle` is the copy folder,
   `bundle_id` the AppUserModelID `Claude.Split.Claude-<Name>`. `theme.conf`
   is generated output.
2. **Bedrock/gateway ("3p") mode per copy.** Fixed. Claude 4.29 still builds
   its 3p folder as `path.join(%LOCALAPPDATA%, "Claude-3p")` for every copy
   (`ju()` in `index.chunk-*.js`; the legacy `%APPDATA%\Claude-3p` too).
   `pin-profile.js` v3 answers exactly that call with `<profile>-3p`
   (`%APPDATA%\Claude-<Name>-3p`), as on the Mac. Verified by running a copy
   with a gateway config: its data went to its own `-3p` folder, its claude.ai
   profile and the shared folder were untouched. `{"deploymentMode":"1p"}`
   pins a copy to claude.ai; `move-3p` hands a setup over (tested).
   `%LOCALAPPDATA%\Claude-3p` now belongs to the stock app only.
3. **Hook v9.** `claude-restyler/windows/inject-hook.js` is `v9-win`: the move
   worker runs in any copy (not only Personal/Work) and reads sessions from
   `app.getPath("userData")` at call time, which is the `-3p` folder in
   gateway mode. The Mac v9 hook was never pushed to claude-restyler, so
   `tests/hook-parity.expected.diff` was re-baselined against the v8 Mac hook;
   push the Mac v9 and rerun `node tests/hook-parity.js`.
4. **Carried over:** routines copy/move (lastRunAt/lastScheduledFor/
   notifySessionId dropped, move disables at the source), MCP merge plus the
   extension files, the update feed, and never touching a running copy.

## How it works

- **Copy**: `robocopy` of a stock build (Store `app\`, the claude.com
  installer's `app-<ver>`, or a feed build) into a staging folder, then a
  folder swap. The copy it replaces is kept as `.Claude <Name>.prev-bak`
  (`multiclaude rollback NAME`).
- **Patch**: `app.asar` is rewritten in C# with `pin-profile.js` at the top of
  the main entry and, when claude-restyler's blackmode theme is installed
  (`~\.config\Claude\blackmode\inject-hook.js`), the hook at the end. The
  `EnableEmbeddedAsarIntegrityValidation` fuse is turned off. No node or
  signing step (Windows doesn't enforce the broken Authenticode signature).
  `multiclaude.json` in the copy records the build.
- **pin-profile.js v3** (runs before Claude's code): profile
  `%APPDATA%\Claude-<Name>`, own 3p folder, AppUserModelID + window icon (the
  `.ico` is watched, so recolors apply live), quit on request
  (`~\.config\multiclaude\quit\<slug>.req`), and the claude:// link hand-off
  file for the link router (MultiClaude's or claude-restyler's).
- **Icon**: the stock art recolored (min(R,G,B) matte, levelled 38%..86% for
  the shaded Windows starburst). Shortcuts point at a copy named after the
  colors so Explorer's icon cache can't keep the old one.
- **Updates**: the tray runs `refresh` at start and every 6 hours: copies
  older than this PC's newest Claude, or carrying an old pin/hook, are
  rebuilt (a running one is staged and swaps in when it quits). **Upgrade
  All** also asks the win32 feed, a Squirrel `RELEASES` line, and downloads the
  `-full.nupkg` (SHA1 checked) when it is newer than anything installed.
- **Deferred ops** run in a detached `MultiClaude.exe _runner` (one at a time,
  named mutex). A copy counts as running while any `claude.exe` under its
  folder lives; locked files are retried.
- From inside a job (a Claude chat's shell), launches go through WMI so the
  copy outlives the shell.

## With claude-restyler

If you used claude-restyler's Windows split (Personal/Work), MultiClaude adopts
those copies on first run (`list`) and rebuilds each one with pin v3: right
away if it is closed, else when you next quit it. claude-restyler's
`apply-blackmode.ps1` (its "Claude Blackmode" scheduled task) hands its runs to
`MultiClaude.exe refresh` once `~\.config\multiclaude\instances.json` exists,
so the two never fight over the copies. The CSS/JS restyle and the migration tool
stay in claude-restyler; both link routers read the same hand-off files.

## Not done / known gaps

- Builds from the update feed lack the Store build's Cowork files
  (`resources\cowork-svc.exe`, `resources\smol-bin.x64.vhdx`,
  `claude-make-data-folder.exe`); Upgrade All says so. Cowork in the copies is
  untested either way.
- Upgrade All's download/unpack path was checked against the package's file
  list, not run end to end (259 MB).
- The link router's registration (Default apps entry) hasn't been exercised
  on a PC without claude-restyler yet; its keys mirror Claude Router's.
- The claude.com-installer (non-Store) path is untested.
- A copy from before MultiClaude can't be asked to quit; **Quit** offers to
  end it instead.
- Unsigned exe: SmartScreen may warn the first time.
