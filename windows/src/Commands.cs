// The engine's commands, its deferred-op queue and runner, MCP/routine copies,
// the 3p hand-off and Upgrade All.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace MultiClaude {

public class Args {
  public string Cmd;
  public List<string> Pos = new List<string>();
  public Dictionary<string, List<string>> Opt = new Dictionary<string, List<string>>();
  static readonly HashSet<string> Multi = new HashSet<string> { "servers", "ids" };
  static readonly HashSet<string> Flags = new HashSet<string> { "json", "no-tint", "launch", "check", "force", "move", "quiet", "dry" };

  public static Args Parse(string[] argv) {
    var a = new Args();
    for (int i = 0; i < argv.Length; i++) {
      var t = argv[i];
      if (t.StartsWith("--")) {
        var k = t.Substring(2); var v = new List<string>();
        int eq = k.IndexOf('=');
        if (eq >= 0) { v.Add(k.Substring(eq + 1)); k = k.Substring(0, eq); }
        else if (Multi.Contains(k)) { while (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) v.Add(argv[++i]); }
        else if (!Flags.Contains(k) && i + 1 < argv.Length) v.Add(argv[++i]);
        a.Opt[k] = v;
      } else if (a.Cmd == null) a.Cmd = t;
      else a.Pos.Add(t);
    }
    return a;
  }

  public bool Has(string k) { return Opt.ContainsKey(k); }
  public string Get(string k) { List<string> v; return Opt.TryGetValue(k, out v) && v.Count > 0 ? v[0] : null; }
  public List<string> All(string k) { List<string> v; return Opt.TryGetValue(k, out v) && v.Count > 0 ? v : null; }
  public string P(int i, string what) {
    if (i >= Pos.Count) throw new McError("missing " + what);
    return Pos[i];
  }
}

public static partial class Engine {
  // ------------------------------------------------------------ pending queue --
  static string OpLabel(Dictionary<string, object> op) {
    string k = S(op, "op"), n = S(op, "name") ?? "", a = S(op, "src") ?? "", b = S(op, "dst") ?? "";
    switch (k) {
      case "swap": return "Finish upgrading Claude " + n;
      case "rebuild": return "Rebuild Claude " + n;
      case "move-3p": return "Move " + a + "'s Bedrock/gateway setup to " + b;
      case "copy-mcp": return "Copy MCP servers " + a + " → " + b;
      case "copy-routines": return (Json.Bool(op, "move", false) ? "Move" : "Copy") + " routines " + a + " → " + b;
    }
    return k + " " + n;
  }

  static readonly object QueueLock = new object();

  static string Key(Dictionary<string, object> x) {
    return string.Join("|", new[] { S(x, "src"), S(x, "dst"), Json.Stringify(x.ContainsKey("servers") ? x["servers"] : null, false),
      Json.Stringify(x.ContainsKey("ids") ? x["ids"] : null, false), Json.Bool(x, "move", false).ToString() });
  }

  public static void Queue(Dictionary<string, object> op) {
    lock (QueueLock) {
      var q = Json.ReadList(PENDING).Select(Json.Obj).Where(x => x != null).ToList();
      if (!op.ContainsKey("id")) op["id"] = Guid.NewGuid().ToString("N").Substring(0, 8);
      if (!op.ContainsKey("queued_at")) op["queued_at"] = DateTime.Now.ToString("s");
      // A newer swap/rebuild of the same instance replaces the older one; an
      // identical copy/move request is not queued twice.
      var kind = S(op, "op");
      Func<Dictionary<string, object>, bool> same = x => S(x, "op") == kind && S(x, "name") == S(op, "name");
      bool replaceable = kind == "swap" || kind == "rebuild";
      if (!replaceable && q.Any(x => same(x) && Key(x) == Key(op))) { Log("already queued: " + kind + " " + S(op, "name")); return; }
      if (replaceable) q = q.Where(x => !same(x)).ToList();
      if (!op.ContainsKey("label")) op["label"] = OpLabel(op);
      q.Add(op);
      Json.Write(PENDING, q);
      Log("queued " + kind + " " + S(op, "name") + " (waits for: " + string.Join(", ", Json.List(op.ContainsKey("wait") ? op["wait"] : null)) + ")");
    }
    EnsureRunner();
  }

  static void Unqueue(string id) {
    lock (QueueLock) {
      var q = Json.ReadList(PENDING).Select(Json.Obj).Where(x => x != null && S(x, "id") != id).ToList();
      Json.Write(PENDING, q);
    }
  }

  const string RUNNER_MUTEX = @"Local\MultiClaude.Runner";

  public static bool RunnerAlive() {
    Mutex m;
    if (Mutex.TryOpenExisting(RUNNER_MUTEX, out m)) { m.Dispose(); return true; }
    return false;
  }

  public static void EnsureRunner() {
    if (RunnerAlive()) return;
    Native.Spawn(SelfExe(), "_runner", true);
  }

  public static void SetStatus(string msg, string kind) {
    Json.Write(STATUS, Json.O("at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "msg", msg, "kind", kind));
  }

  static readonly Dictionary<string, DateTime> AppsClosedAt = new Dictionary<string, DateTime>();

  static bool OpReady(Dictionary<string, object> reg, Dictionary<string, object> op) {
    foreach (var n in Json.List(op.ContainsKey("wait") ? op["wait"] : null)) {
      var inst = Insts(reg).FirstOrDefault(i => S(i, "name") == (string)n);
      if (inst != null && IsRunning(inst)) return false;
    }
    // Stragglers (background Code sessions, crashpad) can outlive a quit
    // indefinitely, so this wait is soft: 90s after the apps are gone.
    var busy = Json.List(op.ContainsKey("wait_profiles") ? op["wait_profiles"] : null).Cast<string>().Where(ProfileBusy).ToList();
    if (busy.Count > 0) {
      var id = S(op, "id");
      if (!AppsClosedAt.ContainsKey(id)) AppsClosedAt[id] = DateTime.UtcNow;
      if ((DateTime.UtcNow - AppsClosedAt[id]).TotalSeconds < 90) return false;
      Log("runner: " + string.Join(", ", busy) + " still has stragglers after 90s; proceeding");
    }
    return true;
  }

  static void ExecOp(Dictionary<string, object> reg, Dictionary<string, object> op) {
    var kind = S(op, "op");
    if (kind == "swap") SwapIn(Find(reg, S(op, "name")), Json.Bool(op, "relaunch", true));
    else if (kind == "rebuild") {
      var inst = Find(reg, S(op, "name"));
      BuildCopy(inst, S(inst, "bundle"), StagedPath(inst));
      SwapIn(inst, Json.Bool(op, "relaunch", false));
    }
    else if (kind == "move-3p") DoMove3p(reg, op);
    else if (kind == "copy-mcp") DoCopyMcp(reg, S(op, "src"), S(op, "dst"), StrList(op, "servers"));
    else if (kind == "copy-routines") DoCopyRoutines(reg, S(op, "src"), S(op, "dst"), StrList(op, "ids"), Json.Bool(op, "move", false));
    else throw new McError("unknown op " + kind);
    foreach (string n in Json.List(op.ContainsKey("launch_after") ? op["launch_after"] : null)) {
      var inst = Find(reg, n);
      ForgetProcs();
      if (Stale(inst) != null && !IsRunning(inst)) {
        // Closed right now anyway: bring its pin/hook current before relaunching.
        try { BuildCopy(inst, S(inst, "bundle"), StagedPath(inst)); SwapIn(inst, false); }
        catch (Exception e) { Log("refresh for " + n + " failed (launching as-is): " + e.Message); }
      }
      Launch(inst);
    }
  }

  // Count a retry on the queued op; false once it has had its five.
  static bool Retry(Dictionary<string, object> op) {
    lock (QueueLock) {
      var q = Json.ReadList(PENDING).Select(Json.Obj).Where(x => x != null).ToList();
      var mine = q.FirstOrDefault(x => S(x, "id") == S(op, "id"));
      if (mine == null) return false;
      int tries = mine.ContainsKey("tries") ? Convert.ToInt32(mine["tries"]) : 0;
      if (tries >= 5) return false;
      mine["tries"] = tries + 1;
      Json.Write(PENDING, q);
      return true;
    }
  }

  static List<string> StrList(Dictionary<string, object> d, string k) {
    if (!d.ContainsKey(k) || d[k] == null) return null;
    var l = Json.List(d[k]).Select(x => Convert.ToString(x)).ToList();
    return l.Count > 0 ? l : null;
  }

  static Dictionary<string, object> CmdRunner(Args a) {
    bool created;
    using (var m = new Mutex(true, RUNNER_MUTEX, out created)) {
      if (!created) return Json.O("ok", true, "msg", "runner already running");
      while (true) {
        var q = Json.ReadList(PENDING).Select(Json.Obj).Where(x => x != null).ToList();
        if (q.Count == 0) break;
        ForgetProcs();
        var reg = LoadRegistry();
        bool progressed = false;
        foreach (var op in q) {
          if (!OpReady(reg, op)) continue;
          Log("runner: executing " + S(op, "op") + " " + S(op, "name") + " [" + S(op, "id") + "]");
          try {
            ExecOp(reg, op);
            SetStatus(S(op, "done_msg") ?? (S(op, "op") + " " + S(op, "name") + " done"), "done");
          } catch (Exception e) {
            if ((e is IOException || e is UnauthorizedAccessException) && Retry(op)) {
              // Something (antivirus, the indexer, an Explorer window) held a
              // file in the folder for a moment: try again shortly.
              Log("runner: " + S(op, "op") + " hit a locked file, retrying: " + e.Message);
              progressed = true;
              Thread.Sleep(10000);
              break;
            }
            Log("runner: " + S(op, "op") + " failed: " + e.Message);
            SetStatus(S(op, "label") + " failed: " + e.Message, "failed");
          }
          Unqueue(S(op, "id"));
          progressed = true;
          break;   // re-read registry/queue after every op
        }
        if (!progressed) Thread.Sleep(4000);
      }
      m.ReleaseMutex();
    }
    return Json.O("ok", true);
  }

  // ------------------------------------------------------------ MCP + routines --
  static readonly string[] MCP_FILES = { "Claude Extensions", "Claude Extensions Settings", "extensions-installations.json", "mcp-user-tool-toggles.json" };

  static string DesktopCfg(Dictionary<string, object> inst) { return Path.Combine(ActiveProfileDir(inst), "claude_desktop_config.json"); }

  static string Ts() { return DateTime.Now.ToString("yyyyMMddHHmmss"); }

  public static Dictionary<string, object> ListMcp(Dictionary<string, object> inst) {
    var servers = Json.Obj(Json.ReadObj(DesktopCfg(inst)).ContainsKey("mcpServers") ? Json.ReadObj(DesktopCfg(inst))["mcpServers"] : null);
    var ed = Path.Combine(ActiveProfileDir(inst), "Claude Extensions");
    var exts = Directory.Exists(ed) ? Directory.GetFileSystemEntries(ed).Select(Path.GetFileName).Where(x => !x.StartsWith(".")).OrderBy(x => x).ToList() : new List<string>();
    return Json.O("servers", servers == null ? new List<string>() : servers.Keys.ToList(), "extensions", exts);
  }

  static void CopyDir(string s, string d) {
    Directory.CreateDirectory(d);
    foreach (var f in Directory.GetFiles(s)) File.Copy(f, Path.Combine(d, Path.GetFileName(f)), true);
    foreach (var sub in Directory.GetDirectories(s)) CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)));
  }

  static void DoCopyMcp(Dictionary<string, object> reg, string srcName, string dstName, List<string> only) {
    var src = Find(reg, srcName); var dst = Find(reg, dstName);
    string sd = ActiveProfileDir(src), dd = ActiveProfileDir(dst);
    Directory.CreateDirectory(dd);
    var scfg = Json.ReadObj(DesktopCfg(src));
    var dpath = DesktopCfg(dst);
    var dcfg = Json.ReadObj(dpath);
    if (File.Exists(dpath)) File.Copy(dpath, dpath + ".bak-multiclaude-" + Ts());
    var servers = Json.Obj(scfg.ContainsKey("mcpServers") ? scfg["mcpServers"] : null) ?? new Dictionary<string, object>();
    if (only != null) servers = servers.Where(kv => only.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    var merged = new Dictionary<string, object>(Json.Obj(dcfg.ContainsKey("mcpServers") ? dcfg["mcpServers"] : null) ?? new Dictionary<string, object>());
    foreach (var kv in servers) merged[kv.Key] = kv.Value;
    dcfg["mcpServers"] = merged;
    Json.Write(dpath, dcfg);
    var copied = servers.Keys.ToList();
    if (only == null) {   // extensions come along on a full copy
      foreach (var n in MCP_FILES) {
        string s = Path.Combine(sd, n), d = Path.Combine(dd, n);
        if (Directory.Exists(s)) { CopyDir(s, d); copied.Add(n + "/"); }
        else if (File.Exists(s) && !File.Exists(d)) { File.Copy(s, d); copied.Add(n); }
      }
    }
    Log("copy-mcp " + S(src, "name") + " -> " + S(dst, "name") + ": " + (copied.Count > 0 ? string.Join(", ", copied) : "nothing to copy"));
  }

  // scheduled-tasks.json files for this instance, newest first. Desktop keeps
  // routines per account+org at <profile>\claude-code-sessions\<account>\<org>\
  // scheduled-tasks.json; the prompt bodies live in the shared ~\.claude.
  static List<string> RoutineFiles(Dictionary<string, object> inst) {
    var root = Path.Combine(ActiveProfileDir(inst), "claude-code-sessions");
    var outList = new List<string>();
    if (Directory.Exists(root))
      foreach (var acct in Directory.GetDirectories(root))
        foreach (var org in Directory.GetDirectories(acct)) {
          var p = Path.Combine(org, "scheduled-tasks.json");
          if (File.Exists(p)) outList.Add(p);
        }
    return outList.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
  }

  static List<Dictionary<string, object>> Tasks(string file) {
    var d = Json.ReadObj(file);
    return Json.List(d.ContainsKey("scheduledTasks") ? d["scheduledTasks"] : null).Select(Json.Obj).Where(x => x != null).ToList();
  }

  public static List<object> ListRoutines(Dictionary<string, object> inst) {
    var rows = new List<object>();
    foreach (var p in RoutineFiles(inst))
      foreach (var t in Tasks(p))
        rows.Add(Json.O("id", S(t, "id"), "name", S(t, "displayName") ?? S(t, "id"), "cron", S(t, "cronExpression"),
          "enabled", Json.Bool(t, "enabled", true), "cwd", S(t, "cwd"), "file", p));
    return rows;
  }

  static void DoCopyRoutines(Dictionary<string, object> reg, string srcName, string dstName, List<string> ids, bool move) {
    var src = Find(reg, srcName); var dst = Find(reg, dstName);
    var sfiles = RoutineFiles(src); var dfiles = RoutineFiles(dst);
    if (dfiles.Count == 0) throw new McError("Claude " + S(dst, "name") + " has no routines store yet: sign in there and open the Code tab once");
    var tasks = sfiles.SelectMany(Tasks).ToList();
    if (ids != null) tasks = tasks.Where(t => ids.Contains(S(t, "id"))).ToList();
    var dpath = dfiles[0];
    var data = Json.ReadObj(dpath);
    var dt = Json.List(data.ContainsKey("scheduledTasks") ? data["scheduledTasks"] : null);
    var have = new HashSet<string>(dt.Select(Json.Obj).Where(x => x != null).Select(x => S(x, "id")));
    File.Copy(dpath, dpath + ".bak-multiclaude-" + Ts());
    var added = new List<string>();
    foreach (var t in tasks) {
      if (have.Contains(S(t, "id"))) continue;
      var nt = t.Where(kv => kv.Key != "lastRunAt" && kv.Key != "lastScheduledFor" && kv.Key != "notifySessionId").ToDictionary(kv => kv.Key, kv => kv.Value);
      nt["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
      dt.Add(nt);
      added.Add(S(t, "id"));
    }
    data["scheduledTasks"] = dt;
    Json.Write(dpath, data);
    if (move && added.Count > 0) {   // disable (never delete) at the source so nothing fires twice
      foreach (var p in sfiles) {
        var sd = Json.ReadObj(p);
        bool changed = false;
        foreach (var t in Json.List(sd.ContainsKey("scheduledTasks") ? sd["scheduledTasks"] : null).Select(Json.Obj)) {
          if (t != null && added.Contains(S(t, "id")) && Json.Bool(t, "enabled", true)) { t["enabled"] = false; changed = true; }
        }
        if (changed) { File.Copy(p, p + ".bak-multiclaude-" + Ts()); Json.Write(p, sd); }
      }
    }
    Log("copy-routines " + S(src, "name") + " -> " + S(dst, "name") + ": added " + (added.Count > 0 ? string.Join(", ", added) : "none") +
      (move && added.Count > 0 ? " (disabled at source)" : ""));
  }

  // Hand a copy's 3p (gateway/Bedrock) setup, Code sessions included, to
  // another copy; the source is pinned back to its claude.ai account.
  static void DoMove3p(Dictionary<string, object> reg, Dictionary<string, object> op) {
    var src = Find(reg, S(op, "src")); var dst = Find(reg, S(op, "dst"));
    foreach (var i in new[] { src, dst })
      if (!HasPinV3(S(i, "bundle"))) throw new McError("Claude " + S(i, "name") + " still shares %LOCALAPPDATA%\\Claude-3p; rebuild it first (refresh)");
    string s3 = ThreePDir(src), d3 = ThreePDir(dst);
    if (ModeOf(dst).StartsWith("3p")) {
      // The target already has its own gateway config; moving would shove it aside.
      Log("move-3p skipped: " + S(dst, "name") + " is already " + ModeOf(dst));
      return;
    }
    if (!Directory.Exists(s3)) throw new McError(s3 + " does not exist");
    if (Directory.Exists(d3)) Directory.Move(d3, d3 + ".pre-move-" + Ts());
    Directory.CreateDirectory(Path.Combine(SUPPORT, S(dst, "profile")));
    Directory.Move(s3, d3);
    Directory.CreateDirectory(s3);
    Json.Write(Path.Combine(s3, "claude_desktop_config.json"), Json.O("deploymentMode", "1p"));
    Log("move-3p: " + s3 + " -> " + d3 + "; " + S(src, "name") + " pinned to 1p");
  }

  // ----------------------------------------------------------------- commands --
  public static Dictionary<string, object> InstView(Dictionary<string, object> i) {
    var v = new Dictionary<string, object>(i);
    var b = S(i, "bundle");
    v["version"] = VersionOf(b);
    v["exists"] = Directory.Exists(b);
    v["running"] = IsRunning(i);
    v["mode"] = ModeOf(i);
    v["staged"] = VersionOf(StagedPath(i));
    v["stale"] = Stale(i);
    v["pin_v3"] = HasPinV3(b);
    v["icon_png"] = IconPng(i);
    v["profile_dir"] = Path.Combine(SUPPORT, S(i, "profile"));
    return v;
  }

  static Dictionary<string, object> CmdList(Args a) {
    var reg = LoadRegistry();
    var pending = Json.ReadList(PENDING);
    if (pending.Count > 0) EnsureRunner();   // survives reboots / a killed runner: the tray polls list
    var rows = Insts(reg).Select(InstView).ToList();
    if (!JsonOut) {
      foreach (var r in rows)
        Console.WriteLine(string.Format("{0} {1,-12} v{2,-12} {3,-10} {4}  {5}{6}{7}", (bool)r["running"] ? "●" : "○", S(r, "name"),
          S(r, "version") ?? "?", S(r, "mode"), S(r, "color"), S(r, "profile"),
          S(r, "staged") != null ? "  staged v" + S(r, "staged") : "", S(r, "stale") != null ? "  [" + S(r, "stale") + "]" : ""));
      foreach (var p in pending.Select(Json.Obj))
        Console.WriteLine("  pending: " + S(p, "label") + " (waits for " + string.Join(", ", Json.List(p["wait"])) + ")");
      return null;
    }
    return Json.O("ok", true, "instances", rows, "pending", pending, "status", Json.ReadObj(STATUS), "latest", Json.ReadObj(LATEST),
      "runner", RunnerAlive());
  }

  static Dictionary<string, object> CmdCreate(Args a) {
    var reg = LoadRegistry();
    Directory.CreateDirectory(APPS);
    var name = Regex.Replace(a.P(0, "name").Trim(), @"\s+", " ");
    if (name.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(7);
    if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9 _-]{0,30}$")) throw Die("name: letters, digits, space, - or _ (max 31)");
    if (Insts(reg).Any(i => Slugify(S(i, "name")) == Slugify(name))) throw Die("instance '" + name + "' already exists");
    if (a.Get("color") == null) throw Die("--color is required");
    var folder = "Claude " + name;
    var inst = Json.O("name", name, "bundle", Path.Combine(APPS, folder), "bundle_id", AUMID_PREFIX + folder.Replace(' ', '-'),
      "profile", folder.Replace(' ', '-'), "color", NormColor(a.Get("color")), "glyph", NormColor(a.Get("glyph") ?? DEFAULT_GLYPH),
      "tint", !a.Has("no-tint"));
    if (Directory.Exists(S(inst, "bundle"))) throw Die(S(inst, "bundle") + " already exists");
    var src = PickSource(reg, a.Get("source"));
    var stage = StagedPath(inst);
    BuildCopy(inst, src, stage);
    Directory.Move(stage, S(inst, "bundle"));
    File.SetAttributes(S(inst, "bundle"), File.GetAttributes(S(inst, "bundle")) & ~FileAttributes.Hidden);
    MakeIcon(inst);
    MakeShortcut(inst);
    var list = Json.List(reg["instances"]); list.Add(inst); reg["instances"] = list;
    SaveRegistry(reg);
    GenTintCss(inst);
    Log("created Claude " + name + " (" + S(inst, "bundle_id") + ", profile " + S(inst, "profile") + ")");
    if (a.Get("copy-mcp-from") != null) DoCopyMcp(reg, a.Get("copy-mcp-from"), name);
    if (a.Has("launch")) Launch(inst);
    return Json.O("ok", true, "instance", InstView(inst));
  }

  static void DoCopyMcp(Dictionary<string, object> reg, string src, string dst) { DoCopyMcp(reg, src, dst, null); }

  static string PickSource(Dictionary<string, object> reg, string source) {
    if (source != null) {
      foreach (var i in Insts(reg)) if (string.Equals(S(i, "name"), source, StringComparison.OrdinalIgnoreCase)) return S(i, "bundle");
      if (VersionOf(source) != null) return source;
      throw Die("source '" + source + "' is neither an instance nor a Claude app folder");
    }
    var best = BestBase();
    if (best == null) throw Die("no Claude found: install Claude (Microsoft Store or claude.com), or run `multiclaude upgrade` to download it");
    return best.App;
  }

  static Dictionary<string, object> CmdRecolor(Args a) {
    var reg = LoadRegistry();
    var inst = Find(reg, a.P(0, "name"));
    var was = S(inst, "color") + Json.Bool(inst, "tint", true);
    if (a.Get("color") != null) inst["color"] = NormColor(a.Get("color"));
    if (a.Get("glyph") != null) inst["glyph"] = NormColor(a.Get("glyph"));
    if (a.Get("tint") != null) inst["tint"] = a.Get("tint") == "on";
    // A hand-written tint (custom-claude-work.css) is only replaced once the
    // color or the tint switch actually changes.
    if (S(inst, "color") + Json.Bool(inst, "tint", true) != was) inst["tint_regen"] = true;
    SaveRegistry(reg);
    GenTintCss(inst);   // live: Ctrl+R in the app picks it up
    MakeIcon(inst);
    MakeShortcut(inst);
    bool live = HasPinV3(S(inst, "bundle"));
    return Json.O("ok", true, "deferred", false, "msg", IsRunning(inst) && !live
      ? "New icon is ready; this copy shows it after it restarts (UI tint: Ctrl+R)"
      : "Claude " + S(inst, "name") + " recolored (UI tint: Ctrl+R in the app)");
  }

  static Dictionary<string, object> CmdRemove(Args a) {
    var reg = LoadRegistry();
    var inst = Find(reg, a.P(0, "name"));
    if (IsRunning(inst)) throw Die("quit Claude " + S(inst, "name") + " first");
    foreach (var p in new[] { S(inst, "bundle"), StagedPath(inst), BackupPath(inst), ShortcutPath(inst) }) Native.Recycle(p);
    foreach (var f in Directory.Exists(ICONS) ? Directory.GetFiles(ICONS, FolderSlug(inst) + ".*") : new string[0]) { try { File.Delete(f); } catch { } }
    reg["instances"] = Insts(reg).Where(i => S(i, "name") != S(inst, "name")).Cast<object>().ToList();
    SaveRegistry(reg);
    Log("removed Claude " + S(inst, "name") + " (app in the Recycle Bin; profile " + S(inst, "profile") + " kept)");
    return Json.O("ok", true, "recycled", S(inst, "bundle"), "profile_kept", Path.Combine(SUPPORT, S(inst, "profile")));
  }

  static Dictionary<string, object> CmdQuit(Args a, bool finish) {
    var inst = Find(LoadRegistry(), a.P(0, "name"));
    if (a.Has("force")) ForceQuit(inst);
    else if (!RequestQuit(inst))
      throw Die("Claude " + S(inst, "name") + " was built before MultiClaude and can't be asked to quit: quit it from its tray icon (or use --force)");
    if (finish) EnsureRunner();
    return Json.O("ok", true);
  }

  // ------------------------------------------------------------------ upgrade --
  static string Arch() {
    try {
      using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
        if (string.Equals(k.GetValue("PROCESSOR_ARCHITECTURE") as string, "ARM64", StringComparison.OrdinalIgnoreCase)) return "arm64";
    } catch { }
    return "x64";
  }

  static string OsVersion() {
    try {
      using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        return k.GetValue("CurrentMajorVersionNumber") + "." + k.GetValue("CurrentMinorVersionNumber") + "." + k.GetValue("CurrentBuildNumber");
    } catch { return "10.0"; }
  }

  // The newest full build in the win32 feed: a Squirrel RELEASES line,
  // "<SHA1> <url> <size>", e.g. AnthropicClaude-4.16336.0-full.nupkg.
  public static Dictionary<string, object> FeedLatest(string cur) {
    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
    var url = "https://api.anthropic.com/api/desktop/win32/" + Arch() + "/squirrel/update?device_id=" + Guid.NewGuid() +
      "&version=" + Uri.EscapeDataString(cur) + "&os_version=" + OsVersion();
    var req = (HttpWebRequest)WebRequest.Create(url);
    req.UserAgent = "multiclaude"; req.Timeout = 30000;
    string body;
    using (var resp = (HttpWebResponse)req.GetResponse()) {
      if (resp.StatusCode == HttpStatusCode.NoContent) return null;
      using (var r = new StreamReader(resp.GetResponseStream())) body = r.ReadToEnd();
    }
    Dictionary<string, object> best = null;
    foreach (var line in body.Split('\n')) {
      var m = Regex.Match(line.Trim(), @"^([0-9A-Fa-f]{40})\s+(\S+-(\d+(?:\.\d+)+)-full\.nupkg)\s+(\d+)$");
      if (!m.Success) continue;
      var u = m.Groups[2].Value;
      if (!u.StartsWith("http")) u = "https://downloads.claude.ai/releases/win32/" + Arch() + "/" + u;
      if (best == null || VCmp(m.Groups[3].Value, S(best, "version")) > 0)
        best = Json.O("version", m.Groups[3].Value, "url", u, "sha1", m.Groups[1].Value.ToUpperInvariant(), "size", long.Parse(m.Groups[4].Value));
    }
    return best;
  }

  static string FileSha1(string path) {
    using (var h = SHA1.Create()) using (var f = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "");
  }

  // Download + verify + unpack a feed build into %LOCALAPPDATA%\multiclaude\base\<ver>.
  static string FetchBase(Dictionary<string, object> upd) {
    var ver = S(upd, "version");
    var baseDir = Path.Combine(CACHE, "base", ver);
    if (VersionOf(baseDir) != null) return baseDir;
    Directory.CreateDirectory(Path.Combine(CACHE, "zips"));
    var z = Path.Combine(CACHE, "zips", "AnthropicClaude-" + ver + "-full.nupkg");
    if (!File.Exists(z) || FileSha1(z) != S(upd, "sha1")) {
      SetStatus("downloading Claude " + ver + "…", "busy");
      Log("downloading " + S(upd, "url"));
      using (var wc = new WebClient()) {
        wc.Headers[HttpRequestHeader.UserAgent] = "multiclaude";
        wc.DownloadFile(S(upd, "url"), z + ".part");
      }
      if (File.Exists(z)) File.Delete(z);
      File.Move(z + ".part", z);
    }
    var h = FileSha1(z);
    if (h != S(upd, "sha1")) throw Die("sha1 mismatch for " + z + ": " + h);
    var tmp = baseDir + ".tmp";
    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    const string prefix = "lib/net45/";
    using (var zip = ZipFile.OpenRead(z)) {
      foreach (var e in zip.Entries) {
        var n = Uri.UnescapeDataString(e.FullName.Replace('\\', '/'));
        if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || n.EndsWith("/")) continue;
        var dest = Path.GetFullPath(Path.Combine(tmp, n.Substring(prefix.Length).Replace('/', '\\')));
        if (!dest.StartsWith(Path.GetFullPath(tmp) + "\\", StringComparison.OrdinalIgnoreCase)) throw Die("bad path in package: " + n);
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        e.ExtractToFile(dest, true);
      }
    }
    if (VersionOf(tmp) == null) throw Die("package " + z + " has no claude.exe under " + prefix);
    if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
    Directory.Move(tmp, baseDir);
    return baseDir;
  }

  static string MinInstalled(Dictionary<string, object> reg) {
    var vers = Insts(reg).Select(i => VersionOf(S(i, "bundle"))).Where(v => v != null).ToList();
    return vers.Count == 0 ? "0.0.0" : vers.OrderBy(v => v, Comparer<string>.Create(VCmp)).First();
  }

  static Dictionary<string, object> CmdUpgrade(Args a) {
    var reg = LoadRegistry();
    var cur = MinInstalled(reg);
    Dictionary<string, object> upd = null; string feedErr = null;
    try { upd = FeedLatest(cur); } catch (Exception e) { feedErr = e.Message; }
    var local = BestBase();
    string ver; string source;
    if (upd != null && (local == null || VCmp(S(upd, "version"), local.Version) > 0)) { ver = S(upd, "version"); source = "feed"; }
    else if (local != null) { ver = local.Version; source = local.Kind; }
    else throw Die("no Claude found locally and the update feed failed: " + (feedErr ?? "no update"));
    var latest = Json.O("checked", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "version", ver, "source", source, "installed_min", cur,
      "size", upd == null ? null : upd["size"], "feed_version", upd == null ? null : S(upd, "version"), "feed_error", feedErr,
      "local_version", local == null ? null : local.Version);
    Json.Write(LATEST, latest);
    if (a.Has("check")) { latest["ok"] = true; return latest; }
    var basePath = source == "feed" ? FetchBase(upd) : local.App;
    var targets = a.Pos.Count > 0 ? a.Pos.Select(n => Find(reg, n)).ToList() : Insts(reg);
    var report = new List<object>();
    foreach (var inst in targets) {
      ForgetProcs();
      if (VCmp(VersionOf(S(inst, "bundle")), ver) >= 0 && Stale(inst) == null && !a.Has("force")) {
        report.Add(Json.O("name", S(inst, "name"), "result", "current")); continue;
      }
      report.Add(Json.O("name", S(inst, "name"), "result", Stage(inst, basePath, "Claude " + S(inst, "name") + " upgraded to " + ver)));
    }
    SetStatus("upgrade to " + ver + ": " + string.Join(", ", report.Select(r => S(Json.Obj(r), "name") + " " + S(Json.Obj(r), "result"))), "done");
    return Json.O("ok", true, "version", ver, "report", report);
  }

  // Build a fresh copy next to the live one; swap it in now if the copy is
  // closed, else when it quits (then relaunch it).
  static string Stage(Dictionary<string, object> inst, string basePath, string doneMsg) {
    SetStatus("staging Claude " + S(inst, "name") + " " + VersionOf(basePath) + "…", "busy");
    BuildCopy(inst, basePath, StagedPath(inst));
    if (!IconCurrent(inst)) MakeIcon(inst);
    ForgetProcs();
    if (IsRunning(inst)) {
      Queue(Json.O("op", "swap", "name", S(inst, "name"), "wait", new List<object> { S(inst, "name") }, "relaunch", true, "done_msg", doneMsg));
      return "staged; swaps in when it quits";
    }
    SwapIn(inst, false);
    return "upgraded";
  }

  // The automatic pass (tray start, every 6 h, claude-restyler's scheduled
  // task): no download. Copies older than the newest Claude on this PC, or
  // carrying an old pin/hook, are rebuilt; icons and shortcuts are repaired.
  static Dictionary<string, object> CmdRefresh(Args a) {
    var reg = LoadRegistry();
    var best = BestBase();
    var report = new List<object>();
    var queued = Json.ReadList(PENDING).Select(Json.Obj).Where(x => x != null && S(x, "op") == "swap").Select(x => S(x, "name")).ToList();
    foreach (var inst in Insts(reg)) {
      ForgetProcs();
      var n = S(inst, "name");
      try {
        if (!IconCurrent(inst)) { MakeIcon(inst); MakeShortcut(inst); report.Add(Json.O("name", n, "result", "icon")); }
        else if (File.Exists(Path.Combine(S(inst, "bundle"), "claude.exe")) && !ShortcutsCurrent(inst)) MakeShortcut(inst);
        var why = Stale(inst);
        var older = best != null && VCmp(VersionOf(S(inst, "bundle")), best.Version) < 0;
        if (why == null && !older) continue;
        var staged = StagedPath(inst);
        if (queued.Contains(n) && Stamp(staged).ContainsKey("build") && S(Stamp(staged), "build") == BuildKey() &&
            (best == null || VCmp(VersionOf(staged), best.Version) >= 0)) continue;   // already waiting to swap in
        // A stock build of the same version is pristine and not in use, so
        // rebuild from that rather than from the (maybe running) copy.
        string src = best != null && VCmp(best.Version, VersionOf(S(inst, "bundle"))) >= 0 ? best.App : S(inst, "bundle");
        if (why == "missing" && best == null) continue;
        var ver = VersionOf(src);
        report.Add(Json.O("name", n, "result", Stage(inst, src, "Claude " + n + " updated to " + ver), "why", older ? "update" : why));
      } catch (Exception e) {
        Log("refresh " + n + " failed: " + e.Message);
        report.Add(Json.O("name", n, "result", "failed: " + e.Message));
      }
    }
    if (report.Count > 0) SetStatus("refresh: " + string.Join(", ", report.Select(r => S(Json.Obj(r), "name") + " " + S(Json.Obj(r), "result"))), "done");
    return Json.O("ok", true, "report", report);
  }

  static Dictionary<string, object> CmdRollback(Args a) {
    var inst = Find(LoadRegistry(), a.P(0, "name"));
    var bak = BackupPath(inst);
    if (VersionOf(bak) == null) throw Die("no rollback copy at " + bak);
    if (IsRunning(inst)) throw Die("quit Claude " + S(inst, "name") + " first");
    var live = S(inst, "bundle"); var tmp = live + ".rollback-tmp";
    Directory.Move(live, tmp); Directory.Move(bak, live); Directory.Move(tmp, bak);
    File.SetAttributes(live, File.GetAttributes(live) & ~FileAttributes.Hidden);
    File.SetAttributes(bak, File.GetAttributes(bak) | FileAttributes.Hidden);
    Log("rolled back " + S(inst, "name") + " to v" + VersionOf(live));
    return Json.O("ok", true, "version", VersionOf(live));
  }

  static Dictionary<string, object> CmdCopyMcp(Args a) {
    var reg = LoadRegistry();
    string sn = a.P(0, "source instance"), dn = a.P(1, "target instance");
    var dst = Find(reg, dn); Find(reg, sn);
    var servers = a.All("servers");
    if (IsRunning(dst)) {
      Queue(Json.O("op", "copy-mcp", "name", S(dst, "name"), "src", sn, "dst", dn, "servers", servers,
        "wait", new List<object> { S(dst, "name") }, "launch_after", new List<object> { S(dst, "name") },
        "done_msg", "MCP config copied into Claude " + S(dst, "name")));
      return Json.O("ok", true, "deferred", true, "msg", "runs when Claude " + S(dst, "name") + " quits, then relaunches it");
    }
    DoCopyMcp(reg, sn, dn, servers);
    return Json.O("ok", true, "deferred", false, "msg", "MCP servers copied into Claude " + S(dst, "name"));
  }

  static Dictionary<string, object> CmdCopyRoutines(Args a) {
    var reg = LoadRegistry();
    var src = Find(reg, a.P(0, "source instance")); var dst = Find(reg, a.P(1, "target instance"));
    bool move = a.Has("move");
    var wait = new List<object> { S(dst, "name") };
    if (move) wait.Add(S(src, "name"));
    if (new[] { dst, src }.Any(i => wait.Contains(S(i, "name")) && IsRunning(i))) {
      Queue(Json.O("op", "copy-routines", "name", S(dst, "name"), "src", S(src, "name"), "dst", S(dst, "name"), "ids", a.All("ids"),
        "move", move, "wait", wait, "launch_after", wait, "done_msg", "Routines copied into Claude " + S(dst, "name")));
      return Json.O("ok", true, "deferred", true, "msg", "runs when " + string.Join(" and ", wait.Select(w => "Claude " + w)) + " quit");
    }
    DoCopyRoutines(reg, S(src, "name"), S(dst, "name"), a.All("ids"), move);
    return Json.O("ok", true, "deferred", false, "msg", "Routines copied into Claude " + S(dst, "name"));
  }

  static Dictionary<string, object> CmdMove3p(Args a) {
    var reg = LoadRegistry();
    var src = Find(reg, a.P(0, "source instance")); var dst = Find(reg, a.P(1, "target instance"));
    Queue(Json.O("op", "move-3p", "name", S(dst, "name"), "src", S(src, "name"), "dst", S(dst, "name"),
      "wait", new List<object> { S(src, "name"), S(dst, "name") }, "wait_profiles", new List<object> { S(src, "profile") + "-3p" },
      "launch_after", a.Has("launch") ? new List<object> { S(src, "name"), S(dst, "name") } : new List<object>(),
      "done_msg", "3p setup moved " + S(src, "name") + " -> " + S(dst, "name")));
    return Json.O("ok", true, "deferred", true, "msg", "runs when Claude " + S(src, "name") + " and Claude " + S(dst, "name") + " are both closed");
  }

  static Dictionary<string, object> CmdInfo(Args a) {
    var inst = Find(LoadRegistry(), a.P(0, "name"));
    return Json.O("ok", true, "instance", InstView(inst), "mcp", ListMcp(inst), "routines", ListRoutines(inst),
      "three_p_dir", ThreePDir(inst), "stamp", Stamp(S(inst, "bundle")));
  }

  static Dictionary<string, object> CmdPending(Args a) {
    if (a.Get("cancel") != null) Unqueue(a.Get("cancel"));
    return Json.O("ok", true, "pending", Json.ReadList(PENDING));
  }

  // ---------------------------------------------------------- launch at login --
  const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

  public static bool LoginItem() {
    using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY)) return k != null && k.GetValue("MultiClaude") != null;
  }

  public static void SetLoginItem(bool on) {
    using (var k = Registry.CurrentUser.CreateSubKey(RUN_KEY)) {
      if (on) k.SetValue("MultiClaude", Native.Quote(SelfExe()));
      else k.DeleteValue("MultiClaude", false);
    }
  }

  // ---------------------------------------------------------------- dispatch --
  public const string HELP = @"multiclaude: run any number of separate Claude desktop copies side by side.

  multiclaude list
  multiclaude create NAME --color '#3B2A6B' [--glyph '#FEFCFB'] [--no-tint] [--source NAME|DIR] [--copy-mcp-from NAME] [--launch]
  multiclaude recolor NAME [--color C] [--glyph C] [--tint on|off]
  multiclaude upgrade [--check] [--force] [NAMES...]   newest Claude (update feed or this PC), swaps in on quit
  multiclaude refresh                                   rebuild copies that are behind this PC's Claude, no download
  multiclaude copy-mcp SRC DST [--servers A B] | copy-routines SRC DST [--ids ...] [--move] | move-3p SRC DST [--launch]
  multiclaude pending [--cancel ID] | quit NAME [--force] | finish NAME | launch NAME | remove NAME | info NAME | rollback NAME
  multiclaude login on|off                              start the MultiClaude tray at sign-in
  multiclaude router on|off                             offer MultiClaude as the claude:// handler (pick it in Settings > Default apps)
  (--json on any command: machine-readable reply)";

  public static Dictionary<string, object> Run(string[] argv) {
    var a = Args.Parse(argv);
    Directory.CreateDirectory(MC_DIR);
    switch (a.Cmd) {
      case "list": return CmdList(a);
      case "create": return CmdCreate(a);
      case "recolor": return CmdRecolor(a);
      case "remove": return CmdRemove(a);
      case "launch": Launch(Find(LoadRegistry(), a.P(0, "name"))); return Json.O("ok", true);
      case "quit": return CmdQuit(a, false);
      case "finish": return CmdQuit(a, true);
      case "info": return CmdInfo(a);
      case "upgrade": return CmdUpgrade(a);
      case "refresh": return CmdRefresh(a);
      case "rollback": return CmdRollback(a);
      case "copy-mcp": return CmdCopyMcp(a);
      case "copy-routines": return CmdCopyRoutines(a);
      case "move-3p": return CmdMove3p(a);
      case "pending": return CmdPending(a);
      case "_runner": return CmdRunner(a);
      case "sync": SaveRegistry(LoadRegistry()); return Json.O("ok", true);
      case "_route": return CmdRoute(a);
      case "router": return CmdRouter(a);
      case "login":
        SetLoginItem(a.P(0, "on|off") == "on");
        return Json.O("ok", true, "login", LoginItem());
    }
    throw new McError("unknown command '" + a.Cmd + "'\n\n" + HELP);
  }
}

}
