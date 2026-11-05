// Entry point. The same sources build two programs:
//   MultiClaude.exe   (windows subsystem) the tray app; with arguments it runs
//                     an engine command without a console (the runner, the
//                     scheduled refresh).
//   multiclaude.com   (console, built with /define:CONSOLE) the CLI, so
//                     `multiclaude list` works in any terminal (.com wins over
//                     .exe on PATH, like devenv.com).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("MultiClaude")]
[assembly: AssemblyProduct("MultiClaude")]
[assembly: AssemblyDescription("Run several Claude desktop copies side by side")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace MultiClaude {

static class Program {
  [STAThread]
  static int Main(string[] argv) {
#if CONSOLE
    const bool console = true;
#else
    const bool console = false;
#endif
    if (argv.Length == 0 || argv[0] == "tray") {
      if (console && argv.Length == 0) { Console.WriteLine(Engine.HELP); return 0; }
      return RunTray();
    }
    if (argv[0] == "help" || argv[0] == "--help" || argv[0] == "-h") { Console.WriteLine(Engine.HELP); return 0; }
    if (argv[0] == "version" || argv[0] == "--version") { Console.WriteLine("MultiClaude " + Engine.VERSION); return 0; }
    try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
    if (argv[0] == "_menu") { Console.Write(TrayApp.DumpMenu()); return 0; }
    if (argv[0] == "_windows") { Console.Write(TrayApp.DumpWindows()); return 0; }
    if (argv[0] == "_form") { TrayApp.RenderForm(argv.Length > 2 ? argv[2] : null, argv[1]); return 0; }
    try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
    Engine.JsonOut = argv.Contains("--json");
    if (!Engine.JsonOut && !argv.Contains("--quiet")) Engine.OnLog = m => Console.Error.WriteLine(m);
    try {
      var r = Engine.Run(argv.Where(x => x != "--json" && x != "--quiet").ToArray());
      if (r != null) Print(r);
      return 0;
    } catch (Exception e) {
      var msg = e is McError ? e.Message : e.GetType().Name + ": " + e.Message;
      if (!(e is McError)) Engine.Log("ERR " + e);
      if (Engine.JsonOut) Console.WriteLine(Json.Stringify(Json.O("ok", false, "error", msg), false));
      else Console.Error.WriteLine("multiclaude: " + msg);
      return 1;
    }
  }

  static void Print(Dictionary<string, object> r) {
    if (Engine.JsonOut) { Console.WriteLine(Json.Stringify(r, false)); return; }
    foreach (var kv in r) {
      if (kv.Key == "ok") continue;
      var v = kv.Value;
      Console.WriteLine(kv.Key + ": " + (v is string || v == null ? (string)v : Json.Stringify(v, false)));
    }
  }

  static int RunTray() {
    bool created;
    using (var m = new Mutex(true, @"Local\MultiClaude.Tray", out created)) {
      if (!created) return 0;   // already in the tray
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => Engine.Log("tray: " + e.Exception);
      Application.Run(new TrayApp());
      return 0;
    }
  }
}

}
