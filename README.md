# MultiClaude

Run several copies of the Claude desktop app on one Mac at the same time. Each
copy has its own sign-in, chats, settings and icon color. A typical setup:
Personal, Work, and a third one pointed at a Bedrock / gateway config.

A menubar icon lets you create copies, pick their colors, upgrade all of them to
the latest Claude, copy MCP servers and routines between them, and cycle through
every Claude window with ⌘\`.

> Unofficial. MultiClaude makes copies of Claude.app with a different bundle id
> and re-signs them ad hoc. Your normal `/Applications/Claude.app` is never
> modified. The copies can't update themselves; use **Upgrade All** in the
> MultiClaude menu instead.

## Install

1. Download `MultiClaude-<version>.zip` from the latest
   [release](../../releases/latest), unzip it, and move `MultiClaude.app` to
   `~/Applications` (or `/Applications`).
2. Open it. The app isn't notarized yet, so macOS blocks it the first time.
   Go to **System Settings → Privacy & Security**, scroll down, and click
   **Open Anyway**. Or run:
   ```bash
   xattr -dr com.apple.quarantine ~/Applications/MultiClaude.app
   ```
3. If macOS asks to install the command line developer tools, accept. MultiClaude
   uses the system `python3`.
4. For the ⌘\` window cycler, allow MultiClaude under **System Settings → Privacy
   & Security → Accessibility**. The cycler switches on by itself once you do.
5. Optional: turn on **Launch at Login** in the menu.

Requirements: macOS 14 or newer (Apple silicon or Intel), and Claude installed
in `/Applications`. If Claude isn't installed, use **Upgrade All**, which
downloads it from Anthropic's update feed.

## Use

**New Instance…**: pick a name and colors. You get `~/Applications/Claude <Name>.app`
with its own profile in `~/Library/Application Support/Claude-<Name>`. Open it and
sign in with the account that copy is for. Each copy also appears in the Dock,
Spotlight and ⌘Tab as a separate app.

Each instance's submenu has:

- **Open / Quit**
- **Change Color…**: icon background, starburst color, and an optional UI tint
  (the tint only applies if you use the blackmode theme; see below).
- **Copy MCP Servers To**: `mcpServers` from that copy's
  `claude_desktop_config.json`, plus installed desktop extensions. Connectors are
  tied to your account, so they show up after you sign in anyway. Claude Code
  MCPs (`~/.claude.json`) are already shared by every copy.
- **Copy / Move Routines To**: scheduled tasks. Move turns them off in the source
  copy rather than deleting them. The target copy needs to have been signed in
  with the Code tab opened once.
- **Hand … Setup To** (copies running a Bedrock/gateway config): gives that
  config, and the Code sessions made under it, to another copy. The source copy
  then goes back to its claude.ai account.
- **Show Profile in Finder**, **Remove Instance…** (the app goes to the Trash,
  the profile stays).

**Upgrade All**: downloads the latest Claude from Anthropic's update feed and
checks its sha256, then rebuilds every copy. A copy that's open switches over
the moment you quit it and then relaunches.

**Runs when these apps quit**: changes that can't happen while a copy is open,
like an upgrade, a new icon, or a copy into its profile, wait here. Each one
runs by itself once the copies it names have quit. Its submenu can quit them
now or cancel the change.

### A Bedrock / gateway copy alongside your normal work account

Claude switches a whole profile into gateway mode when you apply a third-party
config in it. That profile then stops using your claude.ai sign-in, which is
why it looks like you got signed out. To keep both:

1. **New Instance…** → name it e.g. `Bedrock`.
2. Open `Claude Bedrock` and apply the gateway/Bedrock config there, the same
   way you did in the original app.
3. If you already applied it in another copy, use that copy's **Hand … Setup To
   → Bedrock** instead. It moves the config over and puts the original copy back
   on its normal account.

## Command line

The engine inside the app is a plain CLI:

```bash
MC=~/Applications/MultiClaude.app/Contents/Resources/multiclaude
$MC list
$MC create Bedrock --color '#3B2A6B' [--glyph '#FEFCFB'] [--no-tint] [--source Work] [--copy-mcp-from Work] [--launch]
$MC recolor Work --color '#15433D'
$MC upgrade --check | upgrade [names…]
$MC copy-mcp Work Bedrock | copy-routines Work Bedrock [--move] | move-3p Work Bedrock --launch
$MC pending [--cancel ID] | quit NAME | launch NAME | remove NAME | info NAME
```

State lives in `~/.config/multiclaude/`: `instances.json`, the `pending.json`
queue, and `multiclaude.log`. Downloads go to `~/Library/Caches/multiclaude/`.

## Build from source

```bash
git clone https://github.com/2-X/multiclaude && cd multiclaude
app/build.sh          # build, install to ~/Applications, launch
app/build.sh dist     # release zip in dist/ (set NOTARY_PROFILE=<notarytool profile> to notarize)
SIGN=- app/build.sh   # ad-hoc signing if you have no Developer ID
```

A locally built app isn't quarantined, so the Open Anyway step doesn't apply.
With ad-hoc signing, the Accessibility grant resets on every rebuild.

## Optional: blackmode theme

If `~/.config/Claude/blackmode/inject-hook.js` exists (the pure-black restyle
from the private `claude-restyler` repo), MultiClaude injects it into every
copy and writes a per-copy UI tint to `~/.config/Claude/custom-claude-<name>.css`.
That path also needs `node` and the `@electron/asar` and `@electron/fuses`
packages (`npm install`). Without the theme, the app bundle is left as Claude
shipped it, apart from the bundle id, icon and launcher.

## How it works

- Each copy is a `ditto` of Claude.app with
  `CFBundleIdentifier = com.anthropic.claudefordesktop.<name>`. Its
  `Contents/MacOS/Claude` becomes a small script that runs the real binary
  with `--user-data-dir=~/Library/Application Support/Claude-<Name>`.
- The icon is the stock art recolored: the starburst is separated by
  min(R,G,B), levelled to 38%..96%, and the original alpha is kept. The
  CoreGraphics renderer is built into the app (`MultiClaude --render-icon`);
  ImageMagick is used when present.
- Copies are deep re-sealed ad hoc with the entitlements Claude needs
  (`entitlements.plist`; without virtualization, Cowork reports a corrupted
  install).
- Gateway mode is per profile: the app keeps that config in `<profile>-3p` and
  uses that folder as its data folder whenever a config is applied there.
  `{"deploymentMode":"1p"}` in `<profile>-3p/claude_desktop_config.json`
  pins a profile to claude.ai.
- Changes that have to wait are run by a detached `multiclaude _runner`, which
  checks for open copies with `ps`. It never uses `pgrep`, because macOS `pgrep`
  hides the caller's own ancestors.
