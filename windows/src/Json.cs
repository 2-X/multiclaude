// JSON in and out. Parsing goes through JavaScriptSerializer (objects come back
// as Dictionary<string, object>, arrays as object[] or ArrayList); writing is
// our own, so files stay indented and non-ASCII text stays readable.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MultiClaude {

public static class Json {
  static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 4096 };

  public static object Parse(string s) { return Ser.DeserializeObject(s); }

  public static object Read(string path, object fallback) {
    try {
      if (!File.Exists(path)) return fallback;
      var v = Parse(File.ReadAllText(path, Encoding.UTF8));
      return v ?? fallback;
    } catch { return fallback; }
  }

  public static Dictionary<string, object> ReadObj(string path) {
    return Read(path, null) as Dictionary<string, object> ?? new Dictionary<string, object>();
  }

  public static List<object> ReadList(string path) { return List(Read(path, null)); }

  public static void Write(string path, object data) {
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    var tmp = path + ".tmp-" + System.Diagnostics.Process.GetCurrentProcess().Id;
    File.WriteAllText(tmp, Stringify(data, true) + "\n", new UTF8Encoding(false));
    if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
  }

  // ---- access helpers -------------------------------------------------------
  public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

  public static List<object> List(object o) {
    var l = new List<object>();
    var e = o as IEnumerable;
    if (e == null || o is string || o is IDictionary) return l;
    foreach (var x in e) l.Add(x);
    return l;
  }

  public static string Str(Dictionary<string, object> d, string k) {
    object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
  }

  public static bool Bool(Dictionary<string, object> d, string k, bool dflt) {
    object v; if (d == null || !d.TryGetValue(k, out v) || v == null) return dflt;
    return v is bool ? (bool)v : dflt;
  }

  public static Dictionary<string, object> O(params object[] kv) {
    var d = new Dictionary<string, object>();
    for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
    return d;
  }

  // ---- writer ---------------------------------------------------------------
  public static string Stringify(object v, bool pretty) {
    var sb = new StringBuilder();
    WriteValue(sb, v, pretty, 0);
    return sb.ToString();
  }

  static void Indent(StringBuilder sb, bool pretty, int level) {
    if (!pretty) return;
    sb.Append('\n').Append(' ', level * 2);
  }

  static void WriteValue(StringBuilder sb, object v, bool pretty, int level) {
    if (v == null) { sb.Append("null"); return; }
    if (v is string) { Quote(sb, (string)v); return; }
    if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
    if (v is char) { Quote(sb, v.ToString()); return; }
    if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort) {
      sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
    }
    if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
    if (v is double || v is float) {
      double d = Convert.ToDouble(v);
      if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
      else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
      return;
    }
    var dict = v as IDictionary;
    if (dict != null) {
      if (dict.Count == 0) { sb.Append("{}"); return; }
      sb.Append('{');
      bool first = true;
      foreach (DictionaryEntry kv in dict) {
        if (!first) sb.Append(',');
        first = false;
        Indent(sb, pretty, level + 1);
        Quote(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
        sb.Append(pretty ? ": " : ":");
        WriteValue(sb, kv.Value, pretty, level + 1);
      }
      Indent(sb, pretty, level);
      sb.Append('}');
      return;
    }
    var en = v as IEnumerable;
    if (en != null) {
      var items = new List<object>();
      foreach (var x in en) items.Add(x);
      if (items.Count == 0) { sb.Append("[]"); return; }
      sb.Append('[');
      for (int i = 0; i < items.Count; i++) {
        if (i > 0) sb.Append(',');
        Indent(sb, pretty, level + 1);
        WriteValue(sb, items[i], pretty, level + 1);
      }
      Indent(sb, pretty, level);
      sb.Append(']');
      return;
    }
    Quote(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
  }

  static void Quote(StringBuilder sb, string s) {
    sb.Append('"');
    foreach (char c in s) {
      switch (c) {
        case '"': sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        case '\b': sb.Append("\\b"); break;
        case '\f': sb.Append("\\f"); break;
        default:
          if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
          else sb.Append(c);
          break;
      }
    }
    sb.Append('"');
  }
}

}
