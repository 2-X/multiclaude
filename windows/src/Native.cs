// Win32 calls: process paths, jobs, starting processes outside our job,
// Recycle Bin, shell notifications, windows and hotkeys for the cycler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiClaude {

public class Proc {
  public int Pid, Ppid;
  public string Exe = "", Cmd = "";
}

public static class Native {
  // ---- processes ------------------------------------------------------------
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
  const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

  public static string ExePath(int pid) {
    var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (h == IntPtr.Zero) return null;
    try {
      var sb = new StringBuilder(1024); int n = sb.Capacity;
      return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
    } finally { CloseHandle(h); }
  }

  // claude.exe processes with their image paths (fast: no WMI).
  public static List<Proc> ClaudeProcesses() {
    var outList = new List<Proc>();
    foreach (var p in Process.GetProcessesByName("claude")) {
      try {
        var exe = ExePath(p.Id);
        if (exe != null) outList.Add(new Proc { Pid = p.Id, Exe = exe });
      } catch { } finally { p.Dispose(); }
    }
    return outList;
  }

  // Every process with path, parent and command line (WMI; slower).
  public static List<Proc> AllProcesses() {
    var outList = new List<Proc>();
    using (var s = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
    using (var res = s.Get()) {
      foreach (ManagementObject mo in res) {
        using (mo) {
          outList.Add(new Proc {
            Pid = Convert.ToInt32(mo["ProcessId"]), Ppid = Convert.ToInt32(mo["ParentProcessId"]),
            Exe = (mo["ExecutablePath"] as string) ?? "", Cmd = (mo["CommandLine"] as string) ?? "" });
        }
      }
    }
    return outList;
  }

  public static bool InJob() {
    bool r;
    return IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out r) && r;
  }

  public static string Quote(string a) {
    if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
    return "\"" + a.Replace("\"", "\\\"") + "\"";
  }

  // Start a process that outlives us. From inside a job (a Claude chat's
  // shell, a scheduled task) a child dies with the job, so go through WMI,
  // whose children belong to nobody; otherwise a plain start.
  public static void Spawn(string exe, string args, bool hidden) {
    var dir = Path.GetDirectoryName(exe);
    if (InJob()) {
      try {
        using (var cls = new ManagementClass("Win32_Process"))
        using (var startupCls = new ManagementClass("Win32_ProcessStartup"))
        using (var startup = startupCls.CreateInstance()) {
          startup["ShowWindow"] = (ushort)(hidden ? 0 : 1);
          var inp = cls.GetMethodParameters("Create");
          inp["CommandLine"] = Quote(exe) + (string.IsNullOrEmpty(args) ? "" : " " + args);
          inp["CurrentDirectory"] = dir;
          inp["ProcessStartupInformation"] = startup;
          using (var outp = cls.InvokeMethod("Create", inp, null)) {
            if (Convert.ToUInt32(outp["ReturnValue"]) == 0) return;
          }
        }
      } catch { }
    }
    // ShellExecute: the child gets none of our handles (a copy started from a
    // terminal would otherwise write its logs into it).
    var psi = new ProcessStartInfo(exe, args ?? "") { UseShellExecute = true, WorkingDirectory = dir,
      WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
    var p = Process.Start(psi);
    if (p != null) p.Dispose();
  }

  // ---- shell ----------------------------------------------------------------
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct SHFILEOPSTRUCT {
    public IntPtr hwnd; public uint wFunc;
    [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
    [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
    public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
  }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
  const uint FO_DELETE = 3;
  const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

  // To the Recycle Bin. Windows asks first if it is too big to recycle.
  public static void Recycle(string path) {
    if (!File.Exists(path) && !Directory.Exists(path)) return;
    var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = Path.GetFullPath(path) + "\0\0",
      fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING) };
    int r = SHFileOperation(ref op);
    if (r != 0 || op.fAnyOperationsAborted) throw new Exception("could not move " + path + " to the Recycle Bin (" + r + ")");
  }

  [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
  public static void IconsChanged(IEnumerable<string> files) {
    foreach (var f in files) SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, f, IntPtr.Zero);
    SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
  }

  // ---- console --------------------------------------------------------------
  [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);

  // ---- windows (cycler) -----------------------------------------------------
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
  [DllImport("user32.dll")] public static extern short VkKeyScan(char ch);
  public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
  [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
  [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

  public const uint GW_OWNER = 4, GA_ROOT = 2, EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
  public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, SW_RESTORE = 9, WM_HOTKEY = 0x312, DWMWA_CLOAKED = 14;
  public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;

  public static string ClassOf(IntPtr h) {
    var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString();
  }

  public static int PidOf(IntPtr h) { int pid; GetWindowThreadProcessId(h, out pid); return pid; }

  // Top-level app windows a person would Alt+Tab to.
  public static bool IsAppWindow(IntPtr h) {
    if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
    if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
    int cloaked;
    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
    return GetWindowTextLength(h) > 0;
  }

  public static List<IntPtr> TopWindows() {
    var l = new List<IntPtr>();
    EnumWindows((h, lp) => { l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
}

}
