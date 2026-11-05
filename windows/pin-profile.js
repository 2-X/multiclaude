/* === CLAUDE-SPLIT-PIN v3 === */
// MultiClaude for Windows. Prepended to the main entry of each copy, so it runs
// BEFORE any of Claude's own code. Jobs:
//
// 1. Profile. Pins the copy to %APPDATA%\<Copy-Name> ("Claude Work" ->
//    Claude-Work) so every way the copy can start (Start menu, pinned taskbar
//    icon, app.relaunch()) lands in the same profile. The Mac does this with a
//    wrapper script; on Windows a wrapper breaks taskbar pinning, and packaged
//    builds ignore CLAUDE_USER_DATA_DIR. An explicit --user-data-dir wins.
//
// 2. Own Bedrock/gateway ("3p") folder. On Windows the app puts 3p state in
//    %LOCALAPPDATA%\Claude-3p whatever the profile is, so applying a gateway
//    config in one copy flipped every copy into gateway mode. Claude builds
//    that path as path.join(LOCALAPPDATA or APPDATA, "Claude-3p"); that one
//    call is answered with <profile>-3p instead, as on the Mac.
//
// 3. Taskbar identity. Own AppUserModelID (Claude.Split.<Copy-Name>) and
//    window icon, so each copy gets its own taskbar button. The icon file is
//    watched: a recolor from MultiClaude shows up without a restart.
//
// 4. Quit on request. MultiClaude writes ~\.config\multiclaude\quit\<slug>.req
//    to quit a copy the normal way (closing the window only hides it to the
//    tray), e.g. so a staged upgrade can swap in.
//
// 5. Link hand-off. Windows gives claude:// links (Google sign-in, connector
//    OAuth) to the Store app. Each time a copy opens the browser it notes that
//    in ~\.config\Claude\blackmode\handoff\<slug>.json for the Claude Router
//    (claude-restyler), which sends the link back to that copy.
;(() => {
  try {
    const { app, shell, BrowserWindow } = require("electron");
    const fs = require("fs");
    const path = require("path");
    const os = require("os");

    // <copies dir>\Claude Work\claude.exe -> "Claude Work" / "Claude-Work" / "claude-work"
    const name = path.basename(path.dirname(process.execPath));
    const slug = name.replace(/ /g, "-");
    const lslug = slug.toLowerCase();
    const MC_DIR = path.join(os.homedir(), ".config", "multiclaude");
    const BM_DIR = path.join(os.homedir(), ".config", "Claude", "blackmode");
    const plog = (m) => {
      try { fs.appendFileSync(path.join(MC_DIR, "pin.log"), new Date().toISOString() + " [" + process.pid + "] " + name + ": " + m + "\n"); } catch {}
    };

    // 1. profile
    let userData;
    if (app.commandLine.hasSwitch("user-data-dir")) {
      userData = app.commandLine.getSwitchValue("user-data-dir");
    } else {
      userData = path.join(app.getPath("appData"), slug);
      app.setPath("userData", userData);
      app.commandLine.appendSwitch("user-data-dir", userData);
    }

    if (process.platform !== "win32") return;

    // 2. own 3p folder
    const own3p = /-3p$/i.test(userData) ? userData : userData + "-3p";
    const sharedRoots = [process.env.LOCALAPPDATA, process.env.APPDATA].filter(Boolean)
      .map((d) => path.resolve(d).toLowerCase());
    const join = path.join;
    let told3p = false;
    path.join = function (...parts) {
      if (parts.length === 2 && typeof parts[1] === "string" && parts[1].toLowerCase() === "claude-3p" &&
          typeof parts[0] === "string" && sharedRoots.includes(path.resolve(parts[0]).toLowerCase())) {
        if (!told3p) { told3p = true; plog("3p folder -> " + own3p); }
        return own3p;
      }
      return join.apply(this, parts);
    };

    // 3. taskbar identity
    const appId = "Claude.Split." + slug;
    const icon = [path.join(MC_DIR, "icons", lslug + ".ico"), path.join(BM_DIR, "icons", lslug + ".ico")]
      .find((p) => fs.existsSync(p));

    const setId = app.setAppUserModelId.bind(app);
    setId(appId);
    // Claude sets its own MSIX-style ID later; keep ours.
    app.setAppUserModelId = () => setId(appId);

    const dress = (win) => {
      try {
        if (icon) win.setIcon(icon);
        win.setAppDetails({
          appId,
          appIconPath: icon || process.execPath,
          appIconIndex: 0,
          relaunchCommand: '"' + process.execPath + '"',
          relaunchDisplayName: name,
        });
      } catch {}
    };
    app.on("browser-window-created", (_e, win) => dress(win));

    // 5. link hand-off
    const openExternal = shell.openExternal.bind(shell);
    shell.openExternal = (url, ...rest) => {
      try {
        if (/^https?:/i.test(String(url))) {
          const dir = path.join(BM_DIR, "handoff");
          fs.mkdirSync(dir, { recursive: true });
          fs.writeFileSync(path.join(dir, lslug + ".json"),
            JSON.stringify({ name, exe: process.execPath, at: Date.now() }));
        }
      } catch {}
      return openExternal(url, ...rest);
    };

    app.whenReady().then(() => {
      // 3. recolors land live
      let iconAt = 0;
      try { if (icon) iconAt = fs.statSync(icon).mtimeMs; } catch {}
      // 4. quit requests (a request older than this process is stale)
      const req = path.join(MC_DIR, "quit", lslug + ".req");
      const started = Date.now() - process.uptime() * 1000;
      const t = setInterval(() => {
        try {
          if (fs.existsSync(req)) {
            const at = fs.statSync(req).mtimeMs;
            try { fs.unlinkSync(req); } catch {}
            if (at >= started - 2000) {
              plog("quit requested by MultiClaude");
              clearInterval(t);
              app.quit();
              return;
            }
          }
          if (icon) {
            const m = fs.statSync(icon).mtimeMs;
            if (m !== iconAt) { iconAt = m; BrowserWindow.getAllWindows().forEach(dress); plog("icon reloaded"); }
          }
        } catch {}
      }, 1500);
      t.unref();
    });
  } catch (e) {
    try { console.error("[multiclaude] pin failed:", e); } catch {}
  }
})();
/* === END CLAUDE-SPLIT-PIN === */
