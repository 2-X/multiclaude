// The notification-area (tray) app: the Windows counterpart of the Mac
// menubar app. It calls the engine in-process; work runs one action at a time
// on a background thread, and the runner's results come back as balloons.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace MultiClaude {

public class TrayApp : ApplicationContext {
  readonly NotifyIcon ni;
  readonly ContextMenuStrip menu = new ContextMenuStrip();
  readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
  readonly SynchronizationContext ui;
  readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 3000 };
  readonly System.Windows.Forms.Timer daily = new System.Windows.Forms.Timer { Interval = 6 * 3600 * 1000 };
  readonly Cycler cycler;
  volatile string busy;
  long statusSeen;
  string iconKey = "";
  Dictionary<string, object> last = new Dictionary<string, object>();
  string notifiedVersion;
  static readonly string SETTINGS = Path.Combine(Engine.MC_DIR, "tray.json");

  public TrayApp() : this(false) { }

  // headless: no tray icon, timers or background refresh (debug dumps).
  public TrayApp(bool headless) {
    ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    Engine.JsonOut = true;
    statusSeen = Convert.ToInt64(Json.ReadObj(Engine.STATUS).ContainsKey("at") ? Json.ReadObj(Engine.STATUS)["at"] : 0L);
    ni = new NotifyIcon { Text = "MultiClaude", ContextMenuStrip = menu, Visible = !headless };
    if (headless) { cycler = new Cycler(() => false, () => Rows()); Reload(); return; }
    menu.Opening += (s, e) => { e.Cancel = false; Build(); };
    ni.MouseUp += (s, e) => {
      if (e.Button == MouseButtons.Left)
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ni, null);
    };
    ni.BalloonTipClicked += (s, e) => { };
    var t = new Thread(() => { foreach (var a in work.GetConsumingEnumerable()) { try { a(); } catch { } } }) { IsBackground = true };
    t.Start();
    cycler = new Cycler(() => Setting("cycler", true), () => Json.List(last.ContainsKey("instances") ? last["instances"] : null).Select(Json.Obj).ToList());
    poll.Tick += (s, e) => Tick();
    poll.Start();
    daily.Tick += (s, e) => Background(false);
    daily.Start();
    Reload();
    cycler.Refresh();
    Background(true);
  }

  // ---- debug: what the menu, dialogs and cycler see -------------------------
  public static string DumpMenu() {
    var app = new TrayApp(true);
    app.Build();
    var sb = new System.Text.StringBuilder();
    Action<ToolStripItemCollection, int> walk = null;
    walk = (items, depth) => {
      foreach (ToolStripItem i in items) {
        var mi = i as ToolStripMenuItem;
        sb.Append(new string(' ', depth * 2)).Append(i is ToolStripSeparator ? "----" : i.Text + (mi != null && mi.Checked ? "  [x]" : "") + (i.Enabled ? "" : "  (disabled)") + (i.Image != null ? "  [icon]" : "")).Append('\n');
        if (mi != null) walk(mi.DropDownItems, depth + 1);
      }
    };
    walk(app.menu.Items, 0);
    app.ni.Dispose(); app.cycler.Dispose();
    return sb.ToString();
  }

  public static void RenderForm(string name, string outPng) {
    var app = new TrayApp(true);
    var rows = app.Rows();
    var inst = name == null ? null : rows.First(r => string.Equals(Engine.S(r, "name"), name, StringComparison.OrdinalIgnoreCase));
    using (var f = new InstanceForm(app, inst, rows)) {
      f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000); f.ShowInTaskbar = false;
      f.Show(); Application.DoEvents();
      using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(outPng); }
      f.Close();
    }
    using (var ic = TrayIcon(rows.Take(3).Select(r => new[] { Engine.S(r, "color"), Engine.S(r, "glyph") }).ToList()))
    using (var b = ic.ToBitmap()) b.Save(Path.ChangeExtension(outPng, ".tray.png"));
    app.ni.Dispose(); app.cycler.Dispose();
  }

  public static string DumpWindows() {
    var app = new TrayApp(true);
    var sb = new System.Text.StringBuilder();
    foreach (var h in app.cycler.Windows())
      sb.Append(h.ToInt64().ToString("X")).Append("  ").Append(Native.ExePath(Native.PidOf(h))).Append('\n');
    app.ni.Dispose(); app.cycler.Dispose();
    return sb.ToString();
  }

  // ---- state ---------------------------------------------------------------
  bool Setting(string k, bool dflt) { return Json.Bool(Json.ReadObj(SETTINGS), k, dflt); }
  void SetSetting(string k, bool v) { var d = Json.ReadObj(SETTINGS); d[k] = v; Json.Write(SETTINGS, d); }

  void Reload() {
    try { last = Engine.Run(new[] { "list", "--json" }) ?? last; } catch (Exception e) { Engine.Log("tray list failed: " + e.Message); }
    UpdateIcon();
  }

  List<Dictionary<string, object>> Rows() {
    return Json.List(last.ContainsKey("instances") ? last["instances"] : null).Select(Json.Obj).Where(x => x != null).ToList();
  }

  string Latest() { return Engine.S(Json.Obj(last.ContainsKey("latest") ? last["latest"] : null), "version"); }

  bool UpdateAvailable() {
    var l = Latest();
    return l != null && Rows().Any(r => Engine.VCmp(Engine.S(r, "version"), l) < 0);
  }

  void Tick() {
    Reload();
    cycler.Refresh();
    var st = Json.ReadObj(Engine.STATUS);
    long at = Convert.ToInt64(st.ContainsKey("at") ? st["at"] : 0L);
    if (at > statusSeen) {
      statusSeen = at;
      var kind = Engine.S(st, "kind");
      if (kind == "done" || kind == "failed")
        ni.ShowBalloonTip(6000, "MultiClaude", Engine.S(st, "msg"), kind == "failed" ? ToolTipIcon.Error : ToolTipIcon.Info);
    }
    var tip = "MultiClaude" + (busy != null ? ": " + busy + "…" : "");
    ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    var l = Latest();
    if (UpdateAvailable() && l != notifiedVersion) {
      notifiedVersion = l;
      ni.ShowBalloonTip(8000, "Claude " + l + " is available", "Upgrade All in the MultiClaude menu rebuilds every copy.", ToolTipIcon.Info);
    }
  }

  // Startup and every 6 h: rebuild copies that fell behind this PC's Claude
  // (no download), then ask the update feed what's newest.
  void Background(bool first) {
    work.Add(() => {
      busy = "Checking copies";
      try { Engine.Run(new[] { "refresh", "--json" }); } catch (Exception e) { Engine.Log("tray refresh failed: " + e.Message); }
      busy = "Checking for updates";
      try { Engine.Run(new[] { "upgrade", "--check", "--json" }); } catch (Exception e) { Engine.Log("tray update check failed: " + e.Message); }
      busy = null;
      ui.Post(_ => Reload(), null);
    });
  }

  // Run an engine command off the UI thread; report the result.
  void Act(string label, string[] args, Action<Dictionary<string, object>> done = null) {
    work.Add(() => {
      busy = label;
      Dictionary<string, object> r = null; string err = null;
      try { r = Engine.Run(args.Concat(new[] { "--json" }).ToArray()); }
      catch (Exception e) { err = e.Message; }
      busy = null;
      ui.Post(_ => {
        Reload();
        if (err != null) MessageBox.Show(err, label + " failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        else if (r != null && Engine.S(r, "msg") != null) ni.ShowBalloonTip(5000, "MultiClaude", Engine.S(r, "msg"), ToolTipIcon.Info);
        if (done != null) done(err == null ? r : null);
      }, null);
    });
  }

  public void ActFromForm(string label, string[] args, Action<string> done) {
    work.Add(() => {
      busy = label;
      string err = null;
      try { Engine.Run(args.Concat(new[] { "--json" }).ToArray()); } catch (Exception e) { err = e.Message; }
      busy = null;
      ui.Post(_ => { Reload(); done(err); }, null);
    });
  }

  static bool Confirm(string title, string info, string ok) {
    return MessageBox.Show(info, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
  }

  // ---- menu ------------------------------------------------------------------
  readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();

  Image Small(string png) {
    if (png == null || !File.Exists(png)) return null;
    var key = png + "|" + File.GetLastWriteTimeUtc(png).Ticks;
    Image img;
    if (iconCache.TryGetValue(key, out img)) return img;
    using (var src = Builder.LoadPng(png)) {
      var b = new Bitmap(16, 16);
      using (var g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, 16, 16); }
      iconCache[key] = b;
      return b;
    }
  }

  static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true) {
    var i = new ToolStripMenuItem(text) { Enabled = enabled };
    if (onClick != null) i.Click += (s, e) => onClick();
    return i;
  }

  void Build() {
    Reload();
    menu.Items.Clear();
    var rows = Rows();
    foreach (var r in rows) menu.Items.Add(InstanceMenu(r, rows));
    if (rows.Count == 0) menu.Items.Add(Item("No copies yet", null, false));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("New Instance…", () => new InstanceForm(this, null, rows).Show()));
    var l = Latest();
    if (UpdateAvailable()) menu.Items.Add(Item("Upgrade All to " + l + "…", UpgradeAll));
    else menu.Items.Add(Item("Check for Updates", CheckUpdates));
    var pending = Json.List(last.ContainsKey("pending") ? last["pending"] : null).Select(Json.Obj).Where(x => x != null).ToList();
    if (pending.Count > 0) {
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(Item("Runs when these apps quit", null, false));
      foreach (var p in pending) {
        var wait = Json.List(p.ContainsKey("wait") ? p["wait"] : null).Select(x => (string)x).ToList();
        var pi = Item((Engine.S(p, "label") ?? Engine.S(p, "op")) + "  ·  after " + string.Join(" + ", wait.Select(w => "Claude " + w)) + " quit", null);
        foreach (var w in wait) { var wn = w; pi.DropDownItems.Add(Item("Quit Claude " + wn + " Now", () => Quit(rows.FirstOrDefault(x => Engine.S(x, "name") == wn), true))); }
        var id = Engine.S(p, "id");
        pi.DropDownItems.Add(Item("Cancel", () => Act("Cancel", new[] { "pending", "--cancel", id })));
        menu.Items.Add(pi);
      }
    }
    var status = Engine.S(Json.Obj(last.ContainsKey("status") ? last["status"] : null), "msg");
    if (busy != null) { menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(Item("⏳ " + busy + "…", null, false)); }
    else if (!string.IsNullOrEmpty(status)) { menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(Item(status.Length > 90 ? status.Substring(0, 90) + "…" : status, null, false)); }
    menu.Items.Add(new ToolStripSeparator());
    var cyc = Item("Window Cycler (Alt+`)", () => { SetSetting("cycler", !Setting("cycler", true)); cycler.Refresh(); });
    cyc.Checked = Setting("cycler", true);
    menu.Items.Add(cyc);
    var login = Item("Launch at Login", () => { try { Engine.SetLoginItem(!Engine.LoginItem()); } catch (Exception e) { MessageBox.Show(e.Message); } });
    login.Checked = Engine.LoginItem();
    menu.Items.Add(login);
    menu.Items.Add(Item("Open Log", () => { if (File.Exists(Engine.LOG)) Process.Start("notepad.exe", Native.Quote(Engine.LOG)); }));
    menu.Items.Add(Item("Quit MultiClaude", () => { ni.Visible = false; cycler.Dispose(); ExitThread(); }));
  }

  ToolStripMenuItem InstanceMenu(Dictionary<string, object> inst, List<Dictionary<string, object>> rows) {
    string name = Engine.S(inst, "name"), mode = Engine.S(inst, "mode") ?? "1p";
    bool running = Json.Bool(inst, "running", false);
    var title = "Claude " + name;
    if (Engine.S(inst, "version") != null) title += "  ·  " + Engine.S(inst, "version");
    if (mode.StartsWith("3p")) title += "  ·  " + mode;
    if (running) title += "  ●";
    var m = new ToolStripMenuItem(title) { Image = Small(Engine.S(inst, "icon_png")) };
    var others = rows.Where(o => Engine.S(o, "name") != name).ToList();
    m.DropDownItems.Add(Item(running ? "Show" : "Open", () => Act("Open", new[] { "launch", name })));
    if (running) m.DropDownItems.Add(Item("Quit", () => Quit(inst, false)));
    m.DropDownItems.Add(new ToolStripSeparator());
    m.DropDownItems.Add(Item("Change Color…", () => new InstanceForm(this, inst, rows).Show()));
    if (others.Count > 0) {
      var mcp = Item("Copy MCP Servers To", null);
      var cr = Item("Copy Routines To", null);
      var mr = Item("Move Routines To", null);
      foreach (var o in others) {
        var on = Engine.S(o, "name");
        mcp.DropDownItems.Add(Item("Claude " + on, () => Act("Copy MCP", new[] { "copy-mcp", name, on })));
        cr.DropDownItems.Add(Item("Claude " + on, () => Act("Copy routines", new[] { "copy-routines", name, on })));
        mr.DropDownItems.Add(Item("Claude " + on, () => {
          if (Confirm("Move routines to Claude " + on + "?", "They're copied there and disabled (not deleted) in Claude " + name + ", so nothing fires twice.", "Move"))
            Act("Move routines", new[] { "copy-routines", name, on, "--move" });
        }));
      }
      m.DropDownItems.Add(mcp); m.DropDownItems.Add(cr); m.DropDownItems.Add(mr);
      if (mode.StartsWith("3p")) {
        var hand = Item("Hand " + mode.Substring(3) + " Setup To", null);
        foreach (var o in others) {
          var on = Engine.S(o, "name");
          hand.DropDownItems.Add(Item("Claude " + on, () => {
            if (Confirm("Move the 3p setup to Claude " + on + "?", "Claude " + name + " goes back to its claude.ai account; Claude " + on +
                " takes over the gateway config and its Code sessions. Runs when both are closed, then reopens them.", "Move"))
              Act("Move 3p", new[] { "move-3p", name, on, "--launch" });
          }));
        }
        m.DropDownItems.Add(hand);
      }
    }
    m.DropDownItems.Add(new ToolStripSeparator());
    var prof = Engine.S(inst, "profile_dir");
    m.DropDownItems.Add(Item("Show Profile in Explorer", () => { if (Directory.Exists(prof)) Process.Start("explorer.exe", Native.Quote(prof)); }));
    var rm = Item("Remove Instance…", () => {
      if (Confirm("Remove Claude " + name + "?", "The app goes to the Recycle Bin. Its profile (chats, settings, sessions) stays in " + prof + ".", "Remove"))
        Act("Remove", new[] { "remove", name });
    }, !running);
    m.DropDownItems.Add(rm);
    return m;
  }

  void Quit(Dictionary<string, object> inst, bool finish) {
    if (inst == null) return;
    var name = Engine.S(inst, "name");
    if (!Json.Bool(inst, "pin_v3", false)) {
      if (MessageBox.Show("Claude " + name + " was built before MultiClaude, so it can't be asked to quit. Quit it from its own tray icon, " +
          "or close it now?\n\nClosing ends it like Task Manager would: anything not yet saved (a message you're typing) is lost.",
          "Quit Claude " + name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
      Act("Quit", new[] { finish ? "finish" : "quit", name, "--force" });
      return;
    }
    Act("Quit", new[] { finish ? "finish" : "quit", name });
  }

  void CheckUpdates() {
    Act("Checking for updates", new[] { "upgrade", "--check" }, r => {
      if (r == null) return;
      if (!UpdateAvailable()) MessageBox.Show("Every copy is on " + (Latest() ?? "the latest") + " or newer.", "Up to date");
    });
  }

  void UpgradeAll() {
    var v = Latest();
    var lt = Json.Obj(last["latest"]);
    var running = Rows().Where(r => Json.Bool(r, "running", false)).Select(r => "Claude " + Engine.S(r, "name")).ToList();
    var info = (Engine.S(lt, "source") == "feed"
      ? "Downloads Claude " + v + " from Anthropic's update feed (" + (Convert.ToInt64((lt != null && lt.ContainsKey("size") ? lt["size"] : null) ?? 0L) / 1048576) + " MB) and rebuilds every copy."
      : "Rebuilds every copy from the Claude " + v + " installed on this PC.");
    if (Engine.S(lt, "source") == "feed" && Engine.LocalBases().Any(b => b.Kind == "store"))
      info += "\n\nThe feed's build doesn't carry the Cowork VM files the Microsoft Store build has. If you use Cowork in a copy, " +
        "wait for the Store to update Claude instead: MultiClaude rebuilds the copies from it by itself.";
    if (running.Count > 0) info += "\n\nRunning now: " + string.Join(", ", running) + ". Each one switches over when you quit it, then reopens.";
    if (Confirm("Upgrade all copies to " + v + "?", info, "Upgrade")) Act("Upgrading to " + v, new[] { "upgrade" });
  }

  // ---- tray icon: the copies' colors as a stack of cards -----------------
  void UpdateIcon() {
    var cols = Rows().Take(3).Select(r => new[] { Engine.S(r, "color") ?? "#D97757", Engine.S(r, "glyph") ?? Engine.DEFAULT_GLYPH }).ToList();
    if (cols.Count == 0) cols.Add(new[] { "#D97757", Engine.DEFAULT_GLYPH });
    var key = string.Join(",", cols.Select(c => c[0] + c[1]));
    if (key == iconKey) return;
    iconKey = key;
    var old = ni.Icon;
    ni.Icon = TrayIcon(cols);
    if (old != null) old.Dispose();
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

  public static Icon TrayIcon(List<string[]> cols) {
    using (var bmp = new Bitmap(32, 32)) {
      using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int n = cols.Count, size = n == 1 ? 30 : n == 2 ? 24 : 21, step = n == 1 ? 0 : (32 - size) / (n - 1);
        for (int i = n - 1; i >= 0; i--) {
          var r = new Rectangle(step * (n - 1 - i), step * i, size - 1, size - 1);
          using (var path = Rounded(r, size / 4))
          using (var fill = new SolidBrush(ColorTranslator.FromHtml(cols[i][0])))
          using (var edge = new Pen(Color.FromArgb(170, 255, 255, 255), 1.5f)) {
            g.FillPath(fill, path); g.DrawPath(edge, path);
          }
          if (i == 0) {
            var c = new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
            using (var pen = new Pen(ColorTranslator.FromHtml(cols[i][1]), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
              for (int k = 0; k < 4; k++) {
                double a = Math.PI / 4 * k; float d = size * 0.3f;
                g.DrawLine(pen, c.X - (float)Math.Cos(a) * d, c.Y - (float)Math.Sin(a) * d, c.X + (float)Math.Cos(a) * d, c.Y + (float)Math.Sin(a) * d);
              }
            }
          }
        }
      }
      var h = bmp.GetHicon();
      var icon = (Icon)Icon.FromHandle(h).Clone();
      DestroyIcon(h);
      return icon;
    }
  }

  static GraphicsPath Rounded(Rectangle r, int rad) {
    var p = new GraphicsPath(); int d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }
}

// ---- New Instance / Change Color --------------------------------------------
public class InstanceForm : Form {
  static readonly string[] Swatches = { "#000000", "#15433D", "#3B2A6B", "#1F3A5F", "#6B1F2A", "#7A4A12", "#2E2E2E", "#0B5563", "#5B1A5E", "#3C5A14" };
  static readonly string[] GlyphSwatches = { "#FEFCFB", "#FBF8EC", "#D97757", "#F2C14E", "#9AD1D4", "#C9B6F2", "#F2A7B5", "#B8E0A0", "#8A8A8A", "#000000" };
  readonly TrayApp app;
  readonly Dictionary<string, object> inst;   // null = new
  readonly TextBox name = new TextBox();
  readonly CheckBox tint = new CheckBox { Text = "Tint the app UI with the icon color", AutoSize = true, Checked = true };
  readonly CheckBox launch = new CheckBox { Text = "Open it when done", AutoSize = true, Checked = true };
  readonly ComboBox mcp = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
  readonly Label err = new Label { ForeColor = Color.Firebrick, AutoSize = true, MaximumSize = new Size(500, 0) };
  readonly PictureBox preview = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom };
  readonly Label title = new Label { Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11, FontStyle.Bold), AutoSize = true };
  readonly Button ok = new Button { AutoSize = true };
  Color bg = ColorTranslator.FromHtml("#1F3A5F"), glyph = ColorTranslator.FromHtml(Engine.DEFAULT_GLYPH);
  Image art;
  Button bgBtn, glyphBtn;

  public InstanceForm(TrayApp app, Dictionary<string, object> inst, List<Dictionary<string, object>> rows) {
    this.app = app; this.inst = inst;
    Text = inst == null ? "New Claude Instance" : "Change Color: Claude " + Engine.S(inst, "name");
    FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
    StartPosition = FormStartPosition.CenterScreen; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
    Font = SystemFonts.MessageBoxFont; Padding = new Padding(16); ShowIcon = false; TopMost = true;
    try { var sa = Engine.StockArt(); art = new Bitmap(sa[256]); foreach (var i in sa.Values) i.Dispose(); } catch { }
    if (inst != null) {
      bg = ColorTranslator.FromHtml(Engine.S(inst, "color")); glyph = ColorTranslator.FromHtml(Engine.S(inst, "glyph"));
      tint.Checked = Json.Bool(inst, "tint", true);
    }
    var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
    var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
    var headText = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Padding = new Padding(8, 8, 0, 0) };
    headText.Controls.Add(title);
    headText.Controls.Add(new Label { AutoSize = true, ForeColor = SystemColors.GrayText,
      Text = inst == null ? "Own app, own sign-in, own profile. Runs alongside the others." : "The icon updates right away; the UI tint after Ctrl+R in the app." });
    head.Controls.Add(preview); head.Controls.Add(headText);
    col.Controls.Add(head);
    if (inst == null) {
      col.Controls.Add(new Label { Text = "Name (e.g. Bedrock, Client X)", AutoSize = true, Margin = new Padding(3, 10, 3, 0) });
      name.Width = 300;
      name.TextChanged += (s, e) => Redraw();
      col.Controls.Add(name);
    }
    col.Controls.Add(ColorRow("Icon", true));
    col.Controls.Add(ColorRow("Starburst", false));
    col.Controls.Add(tint);
    if (inst == null) {
      var mrow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
      mrow.Controls.Add(new Label { Text = "Copy MCP servers from", AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
      mcp.Items.Add("None");
      foreach (var r in rows) mcp.Items.Add("Claude " + Engine.S(r, "name"));
      mcp.SelectedIndex = 0;
      mrow.Controls.Add(mcp);
      col.Controls.Add(mrow);
      col.Controls.Add(launch);
    }
    col.Controls.Add(err);
    var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
    ok.Text = inst == null ? "Create" : "Apply";
    ok.Click += (s, e) => Submit();
    var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    cancel.Click += (s, e) => Close();
    buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
    col.Controls.Add(buttons);
    Controls.Add(col);
    AcceptButton = ok; CancelButton = cancel;
    Redraw();
  }

  Control ColorRow(string label, bool isBg) {
    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
    row.Controls.Add(new Label { Text = label, Width = 70, Margin = new Padding(3, 6, 3, 0) });
    var pick = new Button { Width = 36, Height = 24, FlatStyle = FlatStyle.Flat };
    pick.Click += (s, e) => {
      using (var d = new ColorDialog { FullOpen = true, Color = isBg ? bg : glyph })
        if (d.ShowDialog(this) == DialogResult.OK) { if (isBg) bg = d.Color; else glyph = d.Color; Redraw(); }
    };
    if (isBg) bgBtn = pick; else glyphBtn = pick;
    row.Controls.Add(pick);
    foreach (var h in isBg ? Swatches : GlyphSwatches) {
      var c = ColorTranslator.FromHtml(h);
      var sw = new Button { Width = 22, Height = 22, FlatStyle = FlatStyle.Flat, BackColor = c, Margin = new Padding(2, 1, 2, 1) };
      sw.FlatAppearance.BorderColor = Color.Gray;
      sw.Click += (s, e) => { if (isBg) bg = c; else glyph = c; Redraw(); };
      row.Controls.Add(sw);
    }
    return row;
  }

  static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }

  void Redraw() {
    title.Text = inst != null ? "Claude " + Engine.S(inst, "name") : "Claude " + (name.Text.Trim().Length > 0 ? name.Text.Trim() : "…");
    if (bgBtn != null) bgBtn.BackColor = bg;
    if (glyphBtn != null) glyphBtn.BackColor = glyph;
    if (art != null) {
      var old = preview.Image;
      preview.Image = Builder.Recolor(art, bg, glyph, 64);
      if (old != null) old.Dispose();
    }
  }

  void Submit() {
    err.Text = "";
    string[] args;
    if (inst == null) {
      var n = name.Text.Trim();
      if (n.Length == 0) { err.Text = "Give it a name."; return; }
      var l = new List<string> { "create", n, "--color", Hex(bg), "--glyph", Hex(glyph) };
      if (!tint.Checked) l.Add("--no-tint");
      if (mcp.SelectedIndex > 0) { l.Add("--copy-mcp-from"); l.Add(((string)mcp.SelectedItem).Substring(7)); }
      if (launch.Checked) l.Add("--launch");
      args = l.ToArray();
    } else {
      args = new[] { "recolor", Engine.S(inst, "name"), "--color", Hex(bg), "--glyph", Hex(glyph), "--tint", tint.Checked ? "on" : "off" };
    }
    ok.Enabled = false; ok.Text = inst == null ? "Building…" : "Applying…";
    app.ActFromForm(inst == null ? "Creating Claude " + name.Text.Trim() : "Recoloring", args, e => {
      if (IsDisposed) return;
      if (e == null) { Close(); return; }
      err.Text = e; ok.Enabled = true; ok.Text = inst == null ? "Create" : "Apply";
    });
  }
}

// ---- Window cycler (Alt+` / Alt+Shift+`) -------------------------------------
// Cycles every Claude window (all copies and the stock app) as if they were one
// app. The hotkeys exist only while a Claude window is in front, so Alt+` keeps
// its meaning everywhere else. A registered copy with no window is opened
// instead (from its tray icon or from scratch).
public class Cycler : NativeWindow, IDisposable {
  readonly Func<bool> enabled;
  readonly Func<List<Dictionary<string, object>>> instances;
  readonly Native.WinEventProc proc;
  IntPtr hook;
  bool registered;
  const int NEXT = 1, PREV = 2;

  public Cycler(Func<bool> enabled, Func<List<Dictionary<string, object>>> instances) {
    this.enabled = enabled; this.instances = instances;
    CreateHandle(new CreateParams());
    proc = (h, ev, hwnd, o, c, t, tm) => Refresh();
    hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    Refresh();
  }

  List<string> Folders() {
    var l = instances().Where(i => i != null).Select(i => Engine.S(i, "bundle")).Where(b => b != null).ToList();
    return l;
  }

  bool IsClaudeExe(string exe) {
    if (string.IsNullOrEmpty(exe) || !exe.EndsWith("\\claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
    if (exe.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0) return true;
    if (exe.IndexOf(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
    return Folders().Any(f => exe.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
  }

  readonly Dictionary<int, string> exeCache = new Dictionary<int, string>();
  string ExeOf(int pid) {
    string e;
    if (!exeCache.TryGetValue(pid, out e)) { e = Native.ExePath(pid); exeCache[pid] = e; if (exeCache.Count > 500) exeCache.Clear(); }
    return e;
  }

  public void Refresh() {
    var fg = Native.GetForegroundWindow();
    bool want = enabled() && fg != IntPtr.Zero && IsClaudeExe(ExeOf(Native.PidOf(fg)));
    if (want == registered) return;
    if (want) {
      uint vk = (uint)(Native.VkKeyScan('`') & 0xFF);
      if (vk == 0xFF) vk = 0xC0;   // VK_OEM_3
      bool a = Native.RegisterHotKey(Handle, NEXT, Native.MOD_ALT | Native.MOD_NOREPEAT, vk);
      bool b = Native.RegisterHotKey(Handle, PREV, Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk);
      registered = a || b;
      if (!a || !b) Engine.Log("cycler: Alt+` hotkey " + (a ? "" : "next ") + (b ? "" : "prev ") + "taken by another app");
    } else {
      Native.UnregisterHotKey(Handle, NEXT); Native.UnregisterHotKey(Handle, PREV);
      registered = false;
    }
  }

  protected override void WndProc(ref Message m) {
    if (m.Msg == Native.WM_HOTKEY) { try { Cycle(m.WParam.ToInt32() == PREV ? -1 : 1); } catch (Exception e) { Engine.Log("cycler: " + e.Message); } return; }
    base.WndProc(ref m);
  }

  public List<IntPtr> Windows() {
    return Native.TopWindows().Where(h => Native.IsAppWindow(h) && Native.ClassOf(h) == "Chrome_WidgetWin_1" && IsClaudeExe(ExeOf(Native.PidOf(h))))
      .OrderBy(h => h.ToInt64()).ToList();
  }

  public void Cycle(int dir) {
    var wins = Windows();
    var present = new HashSet<string>(wins.Select(h => Path.GetDirectoryName(ExeOf(Native.PidOf(h)) ?? "")), StringComparer.OrdinalIgnoreCase);
    foreach (var i in instances()) {
      var b = Engine.S(i, "bundle");
      if (b != null && !present.Contains(b) && File.Exists(Path.Combine(b, "claude.exe"))) { Engine.Launch(i); return; }
    }
    if (wins.Count == 0) return;
    var fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
    int idx = wins.IndexOf(fg);
    var next = wins[((idx + dir) % wins.Count + wins.Count) % wins.Count];
    if (Native.IsIconic(next)) Native.ShowWindow(next, Native.SW_RESTORE);
    Native.SetForegroundWindow(next);
    Native.BringWindowToTop(next);
  }

  public void Dispose() {
    if (hook != IntPtr.Zero) { Native.UnhookWinEvent(hook); hook = IntPtr.Zero; }
    if (registered) { Native.UnregisterHotKey(Handle, NEXT); Native.UnregisterHotKey(Handle, PREV); }
    DestroyHandle();
  }
}

}
