// Low-level pieces of building a copy: the asar repack, the Electron fuse, the
// icon recolor + .ico writer and AppUserModelID shortcuts. Ported from
// claude-restyler's windows/apply-blackmode.ps1 (same recipes, so copies built
// by either look the same). No node, npm or signing step needed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace MultiClaude {

public static class Builder {
  // ---- asar ---------------------------------------------------------------
  // Layout: u32 4 | u32 headerLen | [u32 payloadLen | u32 jsonLen | json | pad4]
  // then the file data; each file entry's "offset" is relative to the data
  // start. Files marked "unpacked" live in app.asar.unpacked and are untouched.
  static Dictionary<string, object> Entry(Dictionary<string, object> header, string path) {
    var node = header;
    foreach (var part in path.Replace('\\', '/').Split('/')) {
      if (part == "" || part == ".") continue;
      node = (Dictionary<string, object>)((Dictionary<string, object>)node["files"])[part];
    }
    return node;
  }

  static void Walk(Dictionary<string, object> node, List<Dictionary<string, object>> outList) {
    object files;
    if (!node.TryGetValue("files", out files)) return;
    foreach (var child in ((Dictionary<string, object>)files).Values) {
      var c = (Dictionary<string, object>)child;
      if (c.ContainsKey("files")) Walk(c, outList);
      else if (c.ContainsKey("offset")) outList.Add(c);
    }
  }

  static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

  static JavaScriptSerializer Ser() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 4096 }; }

  // One packed file's text, reading only the header and that file.
  public static string ReadAsarText(string asarPath, string inner) {
    using (var fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
      var head = new byte[16];
      if (fs.Read(head, 0, 16) != 16) throw new Exception("short asar");
      long headerLen = BitConverter.ToUInt32(head, 4);
      int jsonLen = (int)BitConverter.ToUInt32(head, 12);
      var json = new byte[jsonLen];
      fs.Read(json, 0, jsonLen);
      var header = (Dictionary<string, object>)Ser().DeserializeObject(Encoding.UTF8.GetString(json));
      var e = Entry(header, inner);
      var size = Convert.ToInt32(e["size"]);
      var buf = new byte[size];
      fs.Seek(8 + headerLen + long.Parse((string)e["offset"]), SeekOrigin.Begin);
      int got = 0;
      while (got < size) { int n = fs.Read(buf, got, size - got); if (n <= 0) break; got += n; }
      return Encoding.UTF8.GetString(buf, 0, got);
    }
  }

  // The marker lines our blocks start with, e.g. "CLAUDE-SPLIT-PIN v3".
  public static List<string> AsarMarkers(string asarPath) {
    var outList = new List<string>();
    var pkg = (Dictionary<string, object>)Ser().DeserializeObject(ReadAsarText(asarPath, "package.json"));
    var main = ReadAsarText(asarPath, (string)pkg["main"]);
    foreach (Match m in Regex.Matches(main, @"/\* === (CLAUDE-[A-Z-]+ v[0-9A-Za-z.-]+) === \*/")) outList.Add(m.Groups[1].Value);
    return outList;
  }

  // Puts `pin` after "use strict" at the top of the main entry and `hook` (may
  // be null) at its end. Old blocks are stripped first, so reapplying never
  // stacks; the archive is rewritten with offsets recomputed in their
  // original order.
  public static string PatchAsar(string asarPath, string pin, string hook) {
    var buf = File.ReadAllBytes(asarPath);
    long headerLen = BitConverter.ToUInt32(buf, 4);
    int jsonLen = (int)BitConverter.ToUInt32(buf, 12);
    long dataStart = 8 + headerLen;
    var ser = Ser();
    var header = (Dictionary<string, object>)ser.DeserializeObject(Encoding.UTF8.GetString(buf, 16, jsonLen));

    Func<Dictionary<string, object>, string> read = e => Encoding.UTF8.GetString(buf,
      (int)(dataStart + long.Parse((string)e["offset"])), Convert.ToInt32(e["size"]));
    var pkg = (Dictionary<string, object>)ser.DeserializeObject(read(Entry(header, "package.json")));
    var mainPath = (string)pkg["main"];
    var main = Entry(header, mainPath);
    if (!main.ContainsKey("offset")) throw new Exception("main entry " + mainPath + " is not packed in the asar");

    var src = read(main);
    src = Regex.Replace(src, @"\n?/\* === CLAUDE-SPLIT-PIN[\s\S]*?/\* === END CLAUDE-SPLIT-PIN === \*/\n?", "");
    src = Regex.Replace(src, @"\n?/\* === CLAUDE-BLACKMODE-HOOK[\s\S]*?/\* === END CLAUDE-BLACKMODE-HOOK === \*/\n?", "");
    var strict = Regex.Match(src, "^\\s*([\"'])use strict\\1;?");
    src = strict.Success
      ? strict.Value + "\n" + pin.Trim() + "\n" + src.Substring(strict.Length)
      : pin.Trim() + "\n" + src;
    if (hook != null) src = src + "\n" + hook.Trim() + "\n";
    var newMain = new UTF8Encoding(false).GetBytes(src);

    var entries = new List<Dictionary<string, object>>();
    Walk(header, entries);
    var oldOff = entries.ToDictionary(e => e, e => long.Parse((string)e["offset"]));
    entries = entries.OrderBy(e => oldOff[e]).ToList();
    var moved = new Dictionary<long, long>();   // old offset -> new (asar may share data)
    var order = new List<KeyValuePair<long, Dictionary<string, object>>>();
    long cur = 0;
    foreach (var e in entries) {
      long o = oldOff[e];
      if (e == main) {
        e["offset"] = cur.ToString(); e["size"] = newMain.Length;
        order.Add(new KeyValuePair<long, Dictionary<string, object>>(-1, e)); cur += newMain.Length;
        object integ;
        if (e.TryGetValue("integrity", out integ)) {
          var ig = (Dictionary<string, object>)integ;
          int block = ig.ContainsKey("blockSize") ? Convert.ToInt32(ig["blockSize"]) : 4 * 1024 * 1024;
          using (var sha = SHA256.Create()) {
            ig["hash"] = Hex(sha.ComputeHash(newMain));
            var blocks = new List<object>();
            for (int i = 0; i < newMain.Length; i += block)
              blocks.Add(Hex(sha.ComputeHash(newMain, i, Math.Min(block, newMain.Length - i))));
            ig["blocks"] = blocks.ToArray();
          }
        }
      } else if (moved.ContainsKey(o)) {
        e["offset"] = moved[o].ToString();
      } else {
        moved[o] = cur; e["offset"] = cur.ToString();
        order.Add(new KeyValuePair<long, Dictionary<string, object>>(o, e)); cur += Convert.ToInt64(e["size"]);
      }
    }

    var json = Encoding.UTF8.GetBytes(ser.Serialize(header));
    int pad = (4 - json.Length % 4) % 4;
    var tmp = asarPath + ".split-tmp";
    using (var fs = new FileStream(tmp, FileMode.Create))
    using (var w = new BinaryWriter(fs)) {
      w.Write((uint)4); w.Write((uint)(8 + json.Length + pad));
      w.Write((uint)(4 + json.Length + pad)); w.Write((uint)json.Length);
      w.Write(json); w.Write(new byte[pad]);
      foreach (var kv in order) {
        if (kv.Key < 0) w.Write(newMain);
        else w.Write(buf, (int)(dataStart + kv.Key), Convert.ToInt32(kv.Value["size"]));
      }
    }
    File.Copy(tmp, asarPath, true);
    File.Delete(tmp);
    return mainPath;
  }

  // ---- fuses ---------------------------------------------------------------
  // Electron's fuse wire: the sentinel, a version byte, a length byte, then
  // one '0'/'1' per fuse ('r' = removed). Index 4 in v1 is
  // EnableEmbeddedAsarIntegrityValidation, which must be off for a patched asar.
  public const int FuseAsarIntegrity = 4;

  public static string SetFuse(string exe, int index, bool on) {
    var sentinel = Encoding.ASCII.GetBytes("dL7pKGdnNz796PbbjQWNKmHXBZaB9tsX");
    var b = File.ReadAllBytes(exe);
    int at = -1;
    for (int i = 0; i + sentinel.Length < b.Length && at < 0; i++) {
      if (b[i] != sentinel[0]) continue;
      int j = 1; while (j < sentinel.Length && b[i + j] == sentinel[j]) j++;
      if (j == sentinel.Length) at = i + sentinel.Length;
    }
    if (at < 0) throw new Exception("no Electron fuse wire in " + exe);
    if (b[at] != 1) throw new Exception("unknown fuse wire version " + b[at]);
    if (index >= b[at + 1]) throw new Exception("fuse " + index + " not present");
    long pos = at + 2 + index;
    char was = (char)b[pos];
    if (was == 'r') throw new Exception("fuse " + index + " was removed from this build");
    byte want = (byte)(on ? '1' : '0');
    if (b[pos] != want) using (var fs = new FileStream(exe, FileMode.Open, FileAccess.Write)) { fs.Seek(pos, SeekOrigin.Begin); fs.WriteByte(want); }
    return was + "->" + (char)want;
  }

  // ---- icons ---------------------------------------------------------------
  // min(R,G,B) puts the coral background at 24-35%. The Windows art's
  // starburst is shaded (86-100%, the Mac's is a flat 98%), so the level is
  // 38%..86% here, not the Mac's 38%..96%; anything higher lets the shading
  // pull the background color into the glyph and it reads gray. Flood bg, lay
  // the star back through the matte, keep the source alpha for the corners.
  public static Bitmap Recolor(Image raw, Color bg, Color star, int size) {
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      g.CompositingMode = CompositingMode.SourceCopy;
      g.DrawImage(raw, 0, 0, size, size);
    }
    var rect = new Rectangle(0, 0, size, size);
    var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[data.Stride * size];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    for (int i = 0; i < px.Length; i += 4) {
      byte b = px[i], gr = px[i + 1], r = px[i + 2];
      double m = Math.Min(r, Math.Min(gr, b)) / 255.0;
      double t = Math.Max(0, Math.Min(1, (m - 0.38) / (0.86 - 0.38)));
      px[i]     = (byte)Math.Round(bg.B * (1 - t) + star.B * t);
      px[i + 1] = (byte)Math.Round(bg.G * (1 - t) + star.G * t);
      px[i + 2] = (byte)Math.Round(bg.R * (1 - t) + star.R * t);
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
    return bmp;
  }

  // PNG-compressed .ico (Vista+). sources: size -> stock art for that size.
  public static void WriteIco(string outPath, IDictionary<int, Image> sources, Color bg, Color star) {
    var pngs = new List<KeyValuePair<int, byte[]>>();
    foreach (var kv in sources) {
      using (var bmp = Recolor(kv.Value, bg, star, kv.Key))
      using (var ms = new MemoryStream()) {
        bmp.Save(ms, ImageFormat.Png);
        pngs.Add(new KeyValuePair<int, byte[]>(kv.Key, ms.ToArray()));
      }
    }
    var tmp = outPath + ".tmp";
    using (var fs = new FileStream(tmp, FileMode.Create))
    using (var w = new BinaryWriter(fs)) {
      w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
      int offset = 6 + 16 * pngs.Count;
      foreach (var p in pngs) {
        w.Write((byte)(p.Key >= 256 ? 0 : p.Key)); w.Write((byte)(p.Key >= 256 ? 0 : p.Key));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(p.Value.Length); w.Write(offset);
        offset += p.Value.Length;
      }
      foreach (var p in pngs) w.Write(p.Value);
    }
    File.Copy(tmp, outPath, true);
    File.Delete(tmp);
  }

  public static void SavePng(string outPath, Image src, Color bg, Color star, int size) {
    using (var bmp = Recolor(src, bg, star, size)) bmp.Save(outPath, ImageFormat.Png);
  }

  // A stock PNG, read into memory so the file isn't held open.
  public static Bitmap LoadPng(string path) {
    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
    using (var img = new Bitmap(ms)) return new Bitmap(img);
  }

  // The app icon inside claude.exe at one size, for installs that ship no
  // per-size PNGs (the claude.com installer, the update feed). The pixels
  // come back through GetDIBits as 32bpp BGRA, with the AND mask as the alpha
  // for icons that have none.
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
  [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr wnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr wnd, IntPtr dc);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
  [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
  [StructLayout(LayoutKind.Sequential)]
  struct IconInfo { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
  [StructLayout(LayoutKind.Sequential)]
  struct BitmapInfoHeader {
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
  }

  static byte[] Pixels(IntPtr dc, IntPtr hbm, int size) {
    var bi = new BitmapInfoHeader { biSize = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)), biWidth = size,
      biHeight = -size /* top-down */, biPlanes = 1, biBitCount = 32 };
    var px = new byte[size * size * 4];
    if (GetDIBits(dc, hbm, 0, (uint)size, px, ref bi, 0) != size) throw new Exception("GetDIBits failed");
    return px;
  }

  public static Bitmap ExtractExeIcon(string exe, int size) {
    var icons = new IntPtr[1]; var ids = new uint[1];
    uint got = PrivateExtractIconsW(exe, 0, size, size, icons, ids, 1, 0);
    if (got == 0 || got == 0xFFFFFFFF || icons[0] == IntPtr.Zero) throw new Exception("no icon in " + exe);
    IconInfo info;
    try { if (!GetIconInfo(icons[0], out info)) throw new Exception("GetIconInfo failed"); }
    finally { DestroyIcon(icons[0]); }
    var dc = GetDC(IntPtr.Zero);
    try {
      if (info.hbmColor == IntPtr.Zero) throw new Exception("monochrome icon in " + exe);
      var px = Pixels(dc, info.hbmColor, size);
      bool hasAlpha = false;
      for (int i = 3; i < px.Length && !hasAlpha; i += 4) hasAlpha = px[i] != 0;
      if (!hasAlpha) {
        var mask = Pixels(dc, info.hbmMask, size);   // white = transparent
        for (int i = 0; i < px.Length; i += 4) px[i + 3] = (byte)(mask[i] != 0 ? 0 : 255);
      }
      var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
      var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      for (int y = 0; y < size; y++) Marshal.Copy(px, y * size * 4, data.Scan0 + y * data.Stride, size * 4);
      bmp.UnlockBits(data);
      return bmp;
    } finally {
      ReleaseDC(IntPtr.Zero, dc);
      DeleteObject(info.hbmColor); DeleteObject(info.hbmMask);
    }
  }

  // ---- shortcuts -----------------------------------------------------------
  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellLinkW {
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder f, int c, IntPtr fd, uint fl);
    void GetIDList(out IntPtr p); void SetIDList(IntPtr p);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
    void GetHotkey(out short h); void SetHotkey(short h);
    void GetShowCmd(out int c); void SetShowCmd(int c);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder s, int c, out int i);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, uint r);
    void Resolve(IntPtr h, uint f);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
  }
  [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IPropertyStore {
    void GetCount(out uint c); void GetAt(uint i, out PropertyKey k);
    void GetValue(ref PropertyKey k, out PropVariant v);
    void SetValue(ref PropertyKey k, ref PropVariant v);
    void Commit();
  }
  [StructLayout(LayoutKind.Sequential, Pack = 4)]
  struct PropertyKey { public Guid fmtid; public uint pid; }
  [StructLayout(LayoutKind.Explicit, Size = 24)]   // PROPVARIANT is 24 bytes on x64
  struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }
  [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

  static readonly Guid AppUserModelFmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

  public static void WriteShortcut(string lnk, string target, string args, string icon, string appId, string desc) {
    var link = (IShellLinkW)new CShellLink();
    link.SetPath(target);
    if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
    link.SetWorkingDirectory(Path.GetDirectoryName(target));
    if (icon != null) link.SetIconLocation(icon, 0);
    link.SetDescription(desc);
    if (appId != null) {
      var store = (IPropertyStore)link;
      var key = new PropertyKey { fmtid = AppUserModelFmt, pid = 5 };
      var v = new PropVariant { vt = 31 /* VT_LPWSTR */, p = Marshal.StringToCoTaskMemUni(appId) };
      try { store.SetValue(ref key, ref v); store.Commit(); }
      finally { Marshal.FreeCoTaskMem(v.p); }
    }
    ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(lnk, true);
  }

  // Target path, AppUserModelID and icon file of an existing shortcut.
  public static string[] ReadShortcut(string lnk) {
    var link = (IShellLinkW)new CShellLink();
    ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
    var sb = new StringBuilder(1024);
    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
    string appId = null;
    try {
      var key = new PropertyKey { fmtid = AppUserModelFmt, pid = 5 };
      PropVariant v;
      ((IPropertyStore)link).GetValue(ref key, out v);
      if (v.vt == 31 && v.p != IntPtr.Zero) appId = Marshal.PtrToStringUni(v.p);
    } catch { }
    var ib = new StringBuilder(1024); int idx;
    link.GetIconLocation(ib, ib.Capacity, out idx);
    return new[] { sb.ToString(), appId, ib.ToString() };
  }

  // Point an existing shortcut (a pinned taskbar button) at a new icon file.
  public static void SetShortcutIcon(string lnk, string icon) {
    var link = (IShellLinkW)new CShellLink();
    var pf = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
    pf.Load(lnk, 2 /* STGM_READWRITE */);
    link.SetIconLocation(icon, 0);
    pf.Save(lnk, true);
  }
}

}
