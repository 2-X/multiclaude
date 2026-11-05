// The claude:// link router ("MultiClaude" in Settings > Default apps).
// Google sign-in and connector OAuth finish in the browser with a claude://
// link, and Windows gives those to the regular Claude app, not the copy that
// asked. pin-profile.js notes each time a copy opens the browser
// (~\.config\Claude\blackmode\handoff\<slug>.json, the same files
// claude-restyler's Claude Router reads); a link goes to the copy that did so
// most recently, if within 15 minutes, else to the regular Claude app.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MultiClaude {

public static partial class Engine {
  public static readonly string HANDOFF = Path.Combine(BM, "handoff");
  static readonly string ROUTER_LOG = Path.Combine(MC_DIR, "link-router.log");
  const string PROGID = "MultiClaude.Link";

  static void RouterLog(string msg) {
    try { File.AppendAllText(ROUTER_LOG, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") + " " + msg + "\n"); } catch { }
  }

  static Dictionary<string, object> CmdRoute(Args a) {
    var url = a.P(0, "claude:// link");
    bool dry = a.Has("dry");
    var shown = Regex.Replace(url, "[?#].*$", "");   // sign-in links carry codes
    if (!url.StartsWith("claude:", StringComparison.OrdinalIgnoreCase)) { RouterLog("SKIP  not a claude: link: " + shown); throw new McError("not a claude: link"); }
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    Dictionary<string, object> pick = null;
    if (Directory.Exists(HANDOFF))
      foreach (var f in Directory.GetFiles(HANDOFF, "*.json")) {
        var h = Json.ReadObj(f);
        long at; long.TryParse(S(h, "at"), out at);
        if (S(h, "exe") == null || now - at > 15 * 60 * 1000 || !File.Exists(S(h, "exe"))) continue;
        if (pick == null || at > long.Parse(S(pick, "at"))) pick = h;
      }
    string exe, why;
    if (pick != null) {
      exe = S(pick, "exe");
      why = "COPY  " + S(pick, "name") + " <- " + shown + " (browser opened " + (now - long.Parse(S(pick, "at"))) / 1000 + "s ago)";
    } else {
      // The regular app: the Store build's execution alias, else the
      // claude.com installer's launcher stub. Both take the link as argument.
      var alias = Path.Combine(LOCAL, @"Microsoft\WindowsApps\claude-desktop.exe");
      var stub = Path.Combine(LOCAL, @"AnthropicClaude\claude.exe");
      exe = File.Exists(alias) ? alias : File.Exists(stub) ? stub : null;
      if (exe == null) { RouterLog("ERR   no regular Claude app for " + shown); throw new McError("no copy is waiting for this link and the regular Claude app isn't installed"); }
      why = "STOCK " + exe + " <- " + shown;
    }
    if (!dry) { Native.Spawn(exe, Native.Quote(url), false); RouterLog(why); }
    return Json.O("ok", true, "route", why, "dry", dry);
  }

  // Register (or remove) the router as a claude:// handler. Windows only
  // honors a default the person picks, so this makes it pickable in
  // Settings > Default apps; it doesn't take the default itself.
  public static Dictionary<string, object> CmdRouter(Args a) {
    var on = a.P(0, "on|off") == "on";
    var exe = SelfExe();
    if (on) {
      using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + PROGID)) {
        k.SetValue("", "Claude link (MultiClaude)");
        k.SetValue("URL Protocol", "");
        using (var app = k.CreateSubKey("Application")) {
          app.SetValue("ApplicationName", "MultiClaude");
          app.SetValue("ApplicationIcon", exe + ",0");
        }
        using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", exe + ",0");
        using (var cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", Native.Quote(exe) + " _route \"%1\"");
      }
      using (var k = Registry.CurrentUser.CreateSubKey(@"Software\MultiClaude\Capabilities")) {
        k.SetValue("ApplicationName", "MultiClaude");
        k.SetValue("ApplicationDescription", "Sends claude:// sign-in links to the Claude copy that asked for them");
        using (var u = k.CreateSubKey("URLAssociations")) u.SetValue("claude", PROGID);
      }
      using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue("MultiClaude", @"Software\MultiClaude\Capabilities");
    } else {
      Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + PROGID, false);
      Registry.CurrentUser.DeleteSubKeyTree(@"Software\MultiClaude", false);
      using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)) if (k != null) k.DeleteValue("MultiClaude", false);
    }
    return Json.O("ok", true, "router", on, "default", RouterDefault());
  }

  // The ProgId Windows uses for claude:// links right now (null = none picked).
  public static string RouterDefault() {
    foreach (var sub in new[] { "UserChoiceLatest", "UserChoice" }) {
      using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\claude\" + sub + (sub == "UserChoiceLatest" ? @"\ProgId" : ""))) {
        var v = k == null ? null : k.GetValue("ProgId") as string;
        if (v != null) return v;
      }
    }
    return null;
  }
}

}
