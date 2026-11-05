// multiclaude for Windows: run any number of separate Claude desktop copies
// side by side. Port of the Mac engine (../../multiclaude), same commands, same
// --json replies, same registry schema.
//
// Each instance is a standalone copy of Claude's app folder in
// ~\Applications\Claude <Name>, its own profile (%APPDATA%\Claude-<Name>), its
// own Bedrock/gateway folder (%APPDATA%\Claude-<Name>-3p), AppUserModelID
// (Claude.Split.Claude-<Name>), Start menu shortcut and icon color. The pin
// block (pin-profile.js) put at the top of the copy's app.asar does the
// pinning at runtime; the blackmode hook goes at the end when it's installed.
//
// Anything that must not happen while a copy is running (replacing its files,
// profile edits it would overwrite) is queued in pending.json and run by a
// detached `MultiClaude.exe _runner` once the copies it names have quit.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace MultiClaude {

public class McError : Exception { public McError(string m) : base(m) { } }

public static partial class Engine {
  public static readonly string HOME = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  public static readonly string APPS = Path.Combine(HOME, "Applications");
  public static readonly string SUPPORT = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
  public static readonly string LOCAL = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  public static readonly string MC_DIR = Path.Combine(HOME, ".config", "multiclaude");
  public static readonly string REGISTRY = Path.Combine(MC_DIR, "instances.json");
  public static readonly string PENDING = Path.Combine(MC_DIR, "pending.json");
  public static readonly string STATUS = Path.Combine(MC_DIR, "status.json");
  public static readonly string LATEST = Path.Combine(MC_DIR, "latest.json");
  public static readonly string LOG = Path.Combine(MC_DIR, "multiclaude.log");
  public static readonly string ICONS = Path.Combine(MC_DIR, "icons");
  public static readonly string QUIT_DIR = Path.Combine(MC_DIR, "quit");
  public static readonly string CACHE = Path.Combine(LOCAL, "multiclaude");
  public static readonly string START_MENU = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
  public static readonly string PINNED = Path.Combine(SUPPORT, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

  // Optional blackmode theme (claude-restyler). Skipped cleanly when absent.
  public static readonly string CLAUDE_CFG = Path.Combine(HOME, ".config", "Claude");
  public static readonly string BM = Path.Combine(CLAUDE_CFG, "blackmode");
  public static readonly string HOOK = Path.Combine(BM, "inject-hook.js");
  public static readonly string THEME_CONF = Path.Combine(BM, "theme.conf");

  public const string VERSION = "1.0.0";   // also in Program.cs's assembly attributes
  public const string AUMID_PREFIX = "Claude.Split.";
  public const string DEFAULT_GLYPH = "#FEFCFB";
  public const string PIN_MARKER = "CLAUDE-SPLIT-PIN v3";
  public const string STAMP = "multiclaude.json";      // in each copy folder
  public const string BUILD_RECIPE = "win-1";           // bump when the build steps change
  public const string ICON_RECIPE = "icon-win2";        // same recolor as apply-blackmode.ps1
  public static readonly int[] ICON_SIZES = { 16, 20, 24, 32, 40, 48, 64, 256 };
  static readonly int[] EXE_ICON_SIZES = { 16, 24, 32, 48, 64, 256 };

  public static bool JsonOut;
  public static Action<string> OnLog;

  // ---------------------------------------------------------------- utilities --
  public static void Log(string msg) {
    try {
      Directory.CreateDirectory(MC_DIR);
      File.AppendAllText(LOG, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + " [" + Process.GetCurrentProcess().Id + "] " + msg + "\n");
    } catch { }
    if (OnLog != null) OnLog(msg);
  }

  public static Exception Die(string msg) { Log("ERR " + msg); return new McError(msg); }

  public static string S(Dictionary<string, object> d, string k) { return Json.Str(d, k); }

  public static string Slugify(string name) {
    return Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
  }

  // "Claude Work" -> "claude-work": the name pin-profile.js and the hook use.
  public static string FolderSlug(Dictionary<string, object> inst) {
    return Regex.Replace(Path.GetFileName(S(inst, "bundle")).Trim().ToLowerInvariant(), @"\s+", "-");
  }

  public static string NormColor(string c) {
    c = (c ?? "").Trim();
    if (!Regex.IsMatch(c, "^#?[0-9a-fA-F]{6}$")) throw Die("color must be #RRGGBB (got '" + c + "')");
    return "#" + c.TrimStart('#').ToUpperInvariant();
  }

  public static int[] VTuple(string v) {
    if (string.IsNullOrEmpty(v)) return new int[0];
    var parts = Regex.Matches(v, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
    while (parts.Count > 1 && parts[parts.Count - 1] == 0) parts.RemoveAt(parts.Count - 1);
    return parts.ToArray();
  }

  public static int VCmp(string a, string b) {
    var x = VTuple(a); var y = VTuple(b);
    for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
      int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
      if (p != q) return p.CompareTo(q);
    }
    return 0;
  }

  public static string VersionOf(string appDir) {
    try {
      var exe = Path.Combine(appDir ?? "", "claude.exe");
      if (!File.Exists(exe)) return null;
      var v = FileVersionInfo.GetVersionInfo(exe).FileVersion;
      return string.IsNullOrEmpty(v) ? null : v.Trim();
    } catch { return null; }
  }

  static string Sha(string text) {
    using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
  }

  public static string PinText() {
    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("pin-profile.js"))
    using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
  }

  public static string HookText() { return File.Exists(HOOK) ? File.ReadAllText(HOOK, Encoding.UTF8) : null; }

  // What a current copy carries: pin + hook + build recipe.
  public static string BuildKey() {
    var hook = HookText();
    return BUILD_RECIPE + "|" + Sha(PinText()) + "|" + (hook == null ? "nohook" : Sha(hook));
  }

  public static string SelfExe() {
    var p = Process.GetCurrentProcess().MainModule.FileName;
    // multiclaude.com (console) and MultiClaude.exe (tray) sit side by side;
    // detached work goes to the windowless one.
    var gui = Path.Combine(Path.GetDirectoryName(p), "MultiClaude.exe");
    return File.Exists(gui) ? gui : p;
  }

  // ---------------------------------------------------------------- processes --
  static List<Proc> _claude; static DateTime _claudeAt;
  static List<Proc> _all; static DateTime _allAt;
  static readonly object ProcLock = new object();

  public static List<Proc> ClaudeProcs() {
    lock (ProcLock) {
      if (_claude == null || (DateTime.UtcNow - _claudeAt).TotalSeconds > 1.5) { _claude = Native.ClaudeProcesses(); _claudeAt = DateTime.UtcNow; }
      return _claude;
    }
  }

  public static List<Proc> AllProcs() {
    lock (ProcLock) {
      if (_all == null || (DateTime.UtcNow - _allAt).TotalSeconds > 1.5) { _all = Native.AllProcesses(); _allAt = DateTime.UtcNow; }
      return _all;
    }
  }

  public static void ForgetProcs() { lock (ProcLock) { _claude = null; _all = null; } }

  static bool Under(string path, string dir) {
    if (string.IsNullOrEmpty(path)) return false;
    return path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
  }

  public static bool IsRunning(Dictionary<string, object> inst) {
    var b = S(inst, "bundle");
    return ClaudeProcs().Any(p => Under(p.Exe, b));
  }

  // Any process (the app, Code CLIs it started, helpers) living in this profile dir.
  public static bool ProfileBusy(string profileDir) {
    var dir = Path.Combine(SUPPORT, profileDir);
    int me = Process.GetCurrentProcess().Id;
    var needle = dir.TrimEnd('\\') + "\\";
    return AllProcs().Any(p => p.Pid != me && (Under(p.Exe, dir) ||
      p.Cmd.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ||
      p.Cmd.IndexOf("\"" + dir + "\"", StringComparison.OrdinalIgnoreCase) >= 0));
  }

  // ----------------------------------------------------------------- registry --
  public static Dictionary<string, object> LoadRegistry() {
    var reg = Json.Read(REGISTRY, null) as Dictionary<string, object>;
    if (reg == null) {
      reg = Json.O("instances", Adopt());
      SaveRegistry(reg);
    }
    if (!reg.ContainsKey("instances")) reg["instances"] = new List<object>();
    return reg;
  }

  public static List<Dictionary<string, object>> Insts(Dictionary<string, object> reg) {
    return Json.List(reg["instances"]).Select(x => Json.Obj(x)).Where(x => x != null).ToList();
  }

  public static void SaveRegistry(Dictionary<string, object> reg) {
    var insts = Insts(reg);
    reg["instances"] = insts.Cast<object>().ToList();
    Json.Write(REGISTRY, reg);
    SyncThemeConf(insts);
  }

  static Dictionary<string, string[]> ThemeConfColors() {
    var outD = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    try {
      foreach (var raw in File.ReadAllLines(THEME_CONF)) {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith("#")) continue;
        var f = line.Split('|').Select(x => x.Trim()).ToArray();
        if (f.Length >= 2 && f[0].Length > 0)
          outD[f[0]] = new[] { f[1], f.Length > 2 && f[2].Length > 0 ? f[2] : DEFAULT_GLYPH };
      }
    } catch { }
    return outD;
  }

  // theme.conf is generated output now (as on the Mac), so claude-restyler's
  // apply-blackmode.ps1 and anything else reading it agree with us.
  static void SyncThemeConf(List<Dictionary<string, object>> insts) {
    if (!File.Exists(THEME_CONF)) return;
    var lines = File.ReadAllLines(THEME_CONF).ToList();
    var head = lines.Where(l => l.Trim().Length == 0 || l.TrimStart().StartsWith("#")).ToList();
    while (head.Count > 0 && head[head.Count - 1].Trim().Length == 0) head.RemoveAt(head.Count - 1);
    var body = insts.Select(i => Path.GetFileName(S(i, "bundle")) + "|" + S(i, "color") + "|" + S(i, "glyph"));
    var text = string.Join("\n", head.Concat(new[] { "" }).Concat(body)) + "\n";
    if (text != File.ReadAllText(THEME_CONF).Replace("\r\n", "\n")) File.WriteAllText(THEME_CONF, text);
  }

  // First run: register every split copy already in ~\Applications.
  static List<object> Adopt() {
    var colors = ThemeConfColors();
    var outList = new List<object>();
    if (!Directory.Exists(APPS)) return outList;
    foreach (var dir in Directory.GetDirectories(APPS).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
      var fn = Path.GetFileName(dir);
      if (!fn.StartsWith("Claude ") || !File.Exists(Path.Combine(dir, "claude.exe"))) continue;
      var name = fn.Substring(7);
      string[] c;
      if (!colors.TryGetValue(fn, out c)) c = new[] { "#000000", DEFAULT_GLYPH };
      outList.Add(Json.O("name", name, "bundle", dir, "bundle_id", AUMID_PREFIX + fn.Replace(' ', '-'),
        "profile", fn.Replace(' ', '-'), "color", c[0].ToUpperInvariant(), "glyph", c[1].ToUpperInvariant(),
        "tint", c[0].ToUpperInvariant() != "#000000"));
      Log("adopted " + fn);
    }
    return outList;
  }

  public static Dictionary<string, object> Find(Dictionary<string, object> reg, string name) {
    var key = name.Trim();
    if (key.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase)) key = key.Substring(7);
    foreach (var i in Insts(reg))
      if (string.Equals(S(i, "name"), key, StringComparison.OrdinalIgnoreCase) || Slugify(S(i, "name")) == Slugify(key)) return i;
    throw Die("no instance named '" + name + "' (have: " + string.Join(", ", Insts(reg).Select(i => S(i, "name"))) + ")");
  }

  // ------------------------------------------------------------------- copies --
  public static Dictionary<string, object> Stamp(string dir) { return Json.ReadObj(Path.Combine(dir ?? "", STAMP)); }

  // Why this copy needs a rebuild, or null.
  public static string Stale(Dictionary<string, object> inst) {
    var b = S(inst, "bundle");
    if (!File.Exists(Path.Combine(b, "claude.exe"))) return "missing";
    var st = Stamp(b);
    if (S(st, "build") == null) return "built before MultiClaude";
    if (S(st, "build") != BuildKey()) return "pin/hook changed";
    return null;
  }

  public static bool HasPinV3(string dir) {
    var st = Stamp(dir);
    if (S(st, "pin") == PIN_MARKER) return true;
    try { return Builder.AsarMarkers(Path.Combine(dir, "resources", "app.asar")).Contains(PIN_MARKER); } catch { return false; }
  }

  // The 3p folder this copy actually uses: its own with pin v3, the shared
  // %LOCALAPPDATA%\Claude-3p before that.
  public static string ThreePDir(Dictionary<string, object> inst) {
    return HasPinV3(S(inst, "bundle")) ? Path.Combine(SUPPORT, S(inst, "profile") + "-3p") : Path.Combine(LOCAL, "Claude-3p");
  }

  // 1p (claude.ai account) or 3p:<name> (gateway/Bedrock/Vertex config applied).
  public static string ModeOf(Dictionary<string, object> inst) {
    var d3 = ThreePDir(inst);
    var cfg = Json.ReadObj(Path.Combine(d3, "claude_desktop_config.json"));
    if (S(cfg, "deploymentMode") == "1p") return "1p";
    var meta = Json.ReadObj(Path.Combine(d3, "configLibrary", "_meta.json"));
    var applied = S(meta, "appliedId");
    if (applied != null) {
      var entry = Json.List(meta.ContainsKey("entries") ? meta["entries"] : null).Select(Json.Obj)
        .FirstOrDefault(e => e != null && S(e, "id") == applied);
      if (File.Exists(Path.Combine(d3, "configLibrary", applied + ".json")))
        return "3p:" + (S(entry, "name") ?? "custom");
    }
    return "1p";
  }

  public static string ActiveProfileDir(Dictionary<string, object> inst) {
    if (!ModeOf(inst).StartsWith("3p")) return Path.Combine(SUPPORT, S(inst, "profile"));
    return ThreePDir(inst);
  }

  public static string StagedPath(Dictionary<string, object> inst) {
    return Path.Combine(APPS, "." + Path.GetFileName(S(inst, "bundle")) + ".staged");
  }

  public static string BackupPath(Dictionary<string, object> inst) {
    return Path.Combine(APPS, "." + Path.GetFileName(S(inst, "bundle")) + ".prev-bak");
  }

  // ------------------------------------------------------------- stock Claude --
  public class Base { public string Kind, App, Version, Assets; }

  // Every unmodified Claude build on this PC: the Microsoft Store app, the
  // claude.com installer's app-<ver> folders, and builds Upgrade All
  // downloaded from the update feed.
  public static List<Base> LocalBases() {
    var outList = new List<Base>();
    try {
      using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages")) {
        if (k != null) foreach (var n in k.GetSubKeyNames()) {
          if (!n.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) || !n.EndsWith("__pzs8sxrjxfjjc", StringComparison.OrdinalIgnoreCase)) continue;
          using (var pk = k.OpenSubKey(n)) {
            var root = pk == null ? null : pk.GetValue("PackageRootFolder") as string;
            if (root == null) continue;
            var app = Path.Combine(root, "app");
            var v = VersionOf(app);
            if (v != null) outList.Add(new Base { Kind = "store", App = app, Version = v, Assets = Path.Combine(root, "assets") });
          }
        }
      }
    } catch { }
    try {
      var sq = Path.Combine(LOCAL, "AnthropicClaude");
      if (Directory.Exists(sq)) foreach (var d in Directory.GetDirectories(sq, "app-*")) {
        var v = VersionOf(d);
        if (v != null) outList.Add(new Base { Kind = "squirrel", App = d, Version = v });
      }
    } catch { }
    try {
      var bdir = Path.Combine(CACHE, "base");
      if (Directory.Exists(bdir)) foreach (var d in Directory.GetDirectories(bdir)) {
        if (d.EndsWith(".tmp")) continue;
        var v = VersionOf(d);
        if (v != null) outList.Add(new Base { Kind = "feed", App = d, Version = v });
      }
    } catch { }
    return outList;
  }

  public static Base BestBase() {
    Base best = null;
    foreach (var b in LocalBases())
      if (best == null || VCmp(b.Version, best.Version) > 0 || (VCmp(b.Version, best.Version) == 0 && b.Kind == "store" && best.Kind != "store")) best = b;
    return best;
  }

  // ------------------------------------------------------------------- build --
  static void Robocopy(string src, string dest) {
    Directory.CreateDirectory(dest);
    var psi = new ProcessStartInfo("robocopy.exe", Native.Quote(src) + " " + Native.Quote(dest) + " /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2") {
      UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    using (var p = Process.Start(psi)) {
      p.StandardOutput.ReadToEnd(); p.WaitForExit();
      if (p.ExitCode >= 8) throw new McError("robocopy " + src + " failed (" + p.ExitCode + ")");
    }
  }

  // A complete copy for `inst` at `dest` from `src` (a stock build or another
  // copy). Both converge: the pin, hook and fuse are (re)applied, so the result
  // never depends on what the source carried.
  public static void BuildCopy(Dictionary<string, object> inst, string src, string dest) {
    Log("build " + S(inst, "name") + ": " + src + " -> " + dest + " (v" + VersionOf(src) + ")");
    if (!string.Equals(Path.GetFullPath(src).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
      Robocopy(src, dest);
    var stampFile = Path.Combine(dest, STAMP);
    if (File.Exists(stampFile)) File.Delete(stampFile);
    var hook = HookText();
    // Defender scans a freshly copied claude.exe and holds it for a moment.
    Retrying(() => Builder.PatchAsar(Path.Combine(dest, "resources", "app.asar"), PinText(), hook));
    Retrying(() => Builder.SetFuse(Path.Combine(dest, "claude.exe"), Builder.FuseAsarIntegrity, false));
    var markers = Builder.AsarMarkers(Path.Combine(dest, "resources", "app.asar"));
    if (!markers.Contains(PIN_MARKER)) throw new McError("pin marker missing after patch");
    Json.Write(stampFile, Json.O("name", S(inst, "name"), "version", VersionOf(dest), "source", src,
      "build", BuildKey(), "pin", PIN_MARKER, "hook", markers.FirstOrDefault(m => m.StartsWith("CLAUDE-BLACKMODE-HOOK")),
      "built_at", DateTime.Now.ToString("o")));
    if (dest.StartsWith(Path.Combine(APPS, ".")))
      File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden);
    Log("  built OK v" + VersionOf(dest) + (hook != null ? " (with blackmode hook)" : ""));
  }

  static void Retrying(Action step) {
    for (int i = 0; ; i++) {
      try { step(); return; }
      catch (IOException e) {
        if (i >= 12) throw;
        Log("  busy file, retrying: " + e.Message);
        Thread.Sleep(500 * Math.Min(i + 1, 6));
      }
    }
  }

  public static void SwapIn(Dictionary<string, object> inst, bool relaunch) {
    string live = S(inst, "bundle"), stage = StagedPath(inst), bak = BackupPath(inst);
    var want = VersionOf(stage);
    if (want == null) throw new McError("no staged copy at " + stage);
    if (IsRunning(inst)) throw new McError("Claude " + S(inst, "name") + " is running");
    if (Directory.Exists(bak)) Directory.Delete(bak, true);
    if (Directory.Exists(live)) Retrying(() => Directory.Move(live, bak));
    try { Retrying(() => Directory.Move(stage, live)); }
    catch { if (Directory.Exists(bak) && !Directory.Exists(live)) Directory.Move(bak, live); throw; }
    File.SetAttributes(live, File.GetAttributes(live) & ~FileAttributes.Hidden);
    if (Directory.Exists(bak)) File.SetAttributes(bak, File.GetAttributes(bak) | FileAttributes.Hidden);
    if (VersionOf(live) != want) {
      Directory.Delete(live, true); Directory.Move(bak, live);
      throw new McError("post-swap version mismatch for " + S(inst, "name") + "; rolled back");
    }
    Log("swapped " + S(inst, "name") + " -> v" + want + " (rollback copy: " + bak + ")");
    MakeShortcut(inst);
    if (relaunch) Launch(inst);
  }

  // ------------------------------------------------------------ icon + links --
  public static SortedDictionary<int, Image> StockArt() {
    var art = new SortedDictionary<int, Image>();
    var bases = LocalBases();
    var store = bases.Where(b => b.Kind == "store" && b.Assets != null && File.Exists(Path.Combine(b.Assets, "Square44x44Logo.targetsize-256.png")))
      .OrderByDescending(b => VTuple(b.Version), new SeqCmp()).FirstOrDefault();
    if (store != null) {
      var big = Path.Combine(store.Assets, "Square44x44Logo.targetsize-256.png");
      foreach (var s in ICON_SIZES) {
        var p = Path.Combine(store.Assets, "Square44x44Logo.targetsize-" + s + ".png");
        art[s] = Builder.LoadPng(File.Exists(p) ? p : big);
      }
      return art;
    }
    var any = bases.OrderByDescending(b => VTuple(b.Version), new SeqCmp()).FirstOrDefault();
    if (any == null) throw new McError("no Claude install to take the icon art from");
    var exe = Path.Combine(any.App, "claude.exe");
    var have = new Dictionary<int, Bitmap>();
    foreach (var s in EXE_ICON_SIZES) have[s] = Builder.ExtractExeIcon(exe, s);
    foreach (var s in ICON_SIZES) {
      var from = EXE_ICON_SIZES.Where(x => x >= s).OrderBy(x => x).First();
      art[s] = from == s ? (Image)have[s] : new Bitmap(have[from]);
    }
    return art;
  }

  class SeqCmp : IComparer<int[]> {
    public int Compare(int[] x, int[] y) {
      for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
        int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
        if (p != q) return p.CompareTo(q);
      }
      return 0;
    }
  }

  public static string IconIco(Dictionary<string, object> inst) { return Path.Combine(ICONS, FolderSlug(inst) + ".ico"); }
  public static string IconPng(Dictionary<string, object> inst) { return Path.Combine(ICONS, FolderSlug(inst) + ".png"); }

  // <slug>.ico is what the running copy watches; shortcuts get a copy named
  // after the colors, so Explorer's icon cache can't keep showing the old one.
  static string ShortcutIcon(Dictionary<string, object> inst) {
    var key = (S(inst, "color") + S(inst, "glyph")).Replace("#", "").ToLowerInvariant();
    return Path.Combine(ICONS, FolderSlug(inst) + "." + key + ".ico");
  }

  public static void MakeIcon(Dictionary<string, object> inst) {
    var art = StockArt();
    try {
      Directory.CreateDirectory(ICONS);
      var bg = ColorTranslator.FromHtml(S(inst, "color"));
      var star = ColorTranslator.FromHtml(S(inst, "glyph"));
      var slug = FolderSlug(inst);
      foreach (var old in Directory.GetFiles(ICONS, slug + ".*.ico")) { try { File.Delete(old); } catch { } }
      Builder.WriteIco(IconIco(inst), art, bg, star);
      File.Copy(IconIco(inst), ShortcutIcon(inst), true);
      Builder.SavePng(IconPng(inst), art[256], bg, star, 256);
      Json.Write(Path.Combine(ICONS, slug + ".json"), Json.O("recipe", ICON_RECIPE, "color", S(inst, "color"), "glyph", S(inst, "glyph")));
    } finally { foreach (var i in art.Values) i.Dispose(); }
  }

  public static bool IconCurrent(Dictionary<string, object> inst) {
    var st = Json.ReadObj(Path.Combine(ICONS, FolderSlug(inst) + ".json"));
    return File.Exists(IconIco(inst)) && File.Exists(ShortcutIcon(inst)) && S(st, "recipe") == ICON_RECIPE &&
      S(st, "color") == S(inst, "color") && S(st, "glyph") == S(inst, "glyph");
  }

  public static string ShortcutPath(Dictionary<string, object> inst) {
    return Path.Combine(START_MENU, Path.GetFileName(S(inst, "bundle")) + ".lnk");
  }

  // Start menu shortcut with the copy's AppUserModelID, and every pinned
  // taskbar button for the copy pointed at the current icon.
  public static void MakeShortcut(Dictionary<string, object> inst) {
    var exe = Path.Combine(S(inst, "bundle"), "claude.exe");
    var icon = ShortcutIcon(inst);
    var lnk = ShortcutPath(inst);
    Builder.WriteShortcut(lnk, exe, null, icon, S(inst, "bundle_id"), Path.GetFileName(S(inst, "bundle")));
    var touched = new List<string> { lnk };
    foreach (var p in PinnedFor(inst)) {
      try { Builder.SetShortcutIcon(p, icon); touched.Add(p); } catch (Exception e) { Log("could not update pinned " + p + ": " + e.Message); }
    }
    Native.IconsChanged(touched);
  }

  // Taskbar pins of this copy. They are ID-list links with no plain target;
  // the AppUserModelID tells them apart.
  static List<string> PinnedFor(Dictionary<string, object> inst) {
    var outList = new List<string>();
    var exe = Path.Combine(S(inst, "bundle"), "claude.exe");
    if (Directory.Exists(PINNED)) foreach (var p in Directory.GetFiles(PINNED, "*.lnk")) {
      try {
        var info = Builder.ReadShortcut(p);
        if (info[1] == S(inst, "bundle_id") || string.Equals(info[0], exe, StringComparison.OrdinalIgnoreCase)) outList.Add(p);
      } catch { }
    }
    return outList;
  }

  public static bool ShortcutsCurrent(Dictionary<string, object> inst) {
    if (!File.Exists(ShortcutPath(inst))) return false;
    foreach (var p in PinnedFor(inst).Concat(new[] { ShortcutPath(inst) })) {
      try { if (!string.Equals(Builder.ReadShortcut(p)[2], ShortcutIcon(inst), StringComparison.OrdinalIgnoreCase)) return false; }
      catch { return false; }
    }
    return true;
  }

  // ------------------------------------------------------------- launch/quit --
  public static void Launch(Dictionary<string, object> inst) {
    var exe = Path.Combine(S(inst, "bundle"), "claude.exe");
    if (!File.Exists(exe)) throw new McError("Claude " + S(inst, "name") + " has no app at " + exe);
    Native.Spawn(exe, null, false);
    Log("launched " + S(inst, "name"));
  }

  // Ask a copy to quit the normal way. Closing its window only hides it to
  // the tray, so pin v3 watches for this request file and calls app.quit().
  public static bool RequestQuit(Dictionary<string, object> inst) {
    if (!IsRunning(inst)) return true;
    if (!HasPinV3(S(inst, "bundle"))) return false;
    Directory.CreateDirectory(QUIT_DIR);
    File.WriteAllText(Path.Combine(QUIT_DIR, FolderSlug(inst) + ".req"), DateTime.Now.ToString("o"));
    Log("quit requested: " + S(inst, "name"));
    return true;
  }

  // Last resort for a copy built before MultiClaude (no quit channel): end
  // its process tree.
  public static void ForceQuit(Dictionary<string, object> inst) {
    var b = S(inst, "bundle");
    var mine = AllProcs().Where(p => Under(p.Exe, b)).ToList();
    var pids = new HashSet<int>(mine.Select(p => p.Pid));
    foreach (var root in mine.Where(p => !pids.Contains(p.Ppid))) {
      var psi = new ProcessStartInfo("taskkill.exe", "/PID " + root.Pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true };
      using (var p = Process.Start(psi)) p.WaitForExit(15000);
    }
    Log("force-quit " + S(inst, "name") + " (" + pids.Count + " processes)");
    ForgetProcs();
  }

  // ------------------------------------------------------------------ UI tint --
  // Per-copy UI tint: custom.css with its grays pulled toward the color. The
  // blackmode hook prefers ~\.config\Claude\custom-<slug>.css, falling back to
  // custom.css, so a pure-black copy simply has no file.
  public static string GenTintCss(Dictionary<string, object> inst) {
    var bas = Path.Combine(CLAUDE_CFG, "custom.css");
    var outp = Path.Combine(CLAUDE_CFG, "custom-" + FolderSlug(inst) + ".css");
    if (!File.Exists(bas)) return null;
    Func<string> head = () => { using (var r = new StreamReader(outp)) { var c = new char[300]; int n = r.Read(c, 0, 300); return new string(c, 0, n); } };
    if (!Json.Bool(inst, "tint", true) || S(inst, "color") == "#000000") {
      if (File.Exists(outp) && head().Contains("multiclaude")) File.Delete(outp);
      return null;
    }
    if (File.Exists(outp) && !head().Contains("multiclaude") && !Json.Bool(inst, "tint_regen", false))
      return outp;   // hand-written file (e.g. the original custom-claude-work.css)
    var c0 = ColorTranslator.FromHtml(S(inst, "color"));
    double h, l0, s0;
    RgbToHls(c0.R / 255.0, c0.G / 255.0, c0.B / 255.0, out h, out l0, out s0);
    int H = (int)Math.Round(h * 360);
    Func<double, double> dark = l => l < 12 ? 4 + l * 0.55 : l;
    Func<double, int> sat = l => l < 12 ? 45 : 14;
    var css = File.ReadAllText(bas);
    css = Regex.Replace(css, @"\b0 0% (\d+(?:\.\d+)?)%", m => {
      var l = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      return H + " " + sat(l) + "% " + Math.Round(dark(l)).ToString(CultureInfo.InvariantCulture) + "%";
    });
    css = Regex.Replace(css, @"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b(?![0-9a-fA-F])", m => {
      var v = m.Groups[1].Value;
      if (v.Length == 3) v = string.Concat(v.Select(ch => new string(ch, 2)));
      int rr = Convert.ToInt32(v.Substring(0, 2), 16), gg = Convert.ToInt32(v.Substring(2, 2), 16), bb = Convert.ToInt32(v.Substring(4, 2), 16);
      if (!(rr == gg && gg == bb)) return m.Value;
      double l = rr / 255.0 * 100, nl = dark(l) / 100, ns = (l < 12 ? sat(l) : 8) / 100.0;
      double cr, cg, cb;
      HlsToRgb(H / 360.0, nl, ns, out cr, out cg, out cb);
      return string.Format("#{0:x2}{1:x2}{2:x2}", (int)Math.Round(cr * 255), (int)Math.Round(cg * 255), (int)Math.Round(cb * 255));
    });
    var hdr = "/* multiclaude: Claude " + S(inst, "name") + " tint (hue " + H + ", from " + S(inst, "color") + ").\n" +
      "   Generated from custom.css; edits are overwritten on recolor. Ctrl+R to reload. */\n";
    File.WriteAllText(outp, hdr + css);
    return outp;
  }

  // Python's colorsys.rgb_to_hls / hls_to_rgb.
  static void RgbToHls(double r, double g, double b, out double h, out double l, out double s) {
    double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
    l = (minc + maxc) / 2.0;
    if (minc == maxc) { h = 0; s = 0; return; }
    s = l <= 0.5 ? (maxc - minc) / (maxc + minc) : (maxc - minc) / (2.0 - maxc - minc);
    double rc = (maxc - r) / (maxc - minc), gc = (maxc - g) / (maxc - minc), bc = (maxc - b) / (maxc - minc);
    if (r == maxc) h = bc - gc; else if (g == maxc) h = 2.0 + rc - bc; else h = 4.0 + gc - rc;
    h = ((h / 6.0) % 1.0 + 1.0) % 1.0;
  }

  static void HlsToRgb(double h, double l, double s, out double r, out double g, out double b) {
    if (s == 0) { r = g = b = l; return; }
    double m2 = l <= 0.5 ? l * (1.0 + s) : l + s - l * s, m1 = 2.0 * l - m2;
    r = V(m1, m2, h + 1.0 / 3); g = V(m1, m2, h); b = V(m1, m2, h - 1.0 / 3);
  }

  static double V(double m1, double m2, double hue) {
    hue = (hue % 1.0 + 1.0) % 1.0;
    if (hue < 1.0 / 6) return m1 + (m2 - m1) * hue * 6.0;
    if (hue < 0.5) return m2;
    if (hue < 2.0 / 3) return m1 + (m2 - m1) * (2.0 / 3 - hue) * 6.0;
    return m1;
  }
}

}
