using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace GlbExport
{
    /// <summary>
    /// Escritor JSON minimo y sin dependencias. Se evita System.Text.Json a proposito: en Revit 2023/2024
    /// (.NET Framework) trae versiones de System.Runtime.CompilerServices.Unsafe que chocan con las que Revit ya carga.
    /// </summary>
    public sealed class JsonWriter : IDisposable
    {
        private readonly TextWriter _w;
        private readonly Stack<bool> _first = new Stack<bool>();   // true = aun no se escribio un elemento en este nivel

        public JsonWriter(Stream s) { _w = new StreamWriter(s, new UTF8Encoding(false), 65536); }
        public void Dispose() => _w.Dispose();

        private void Sep()
        {
            if (_first.Count == 0) return;
            if (_first.Pop()) _first.Push(false); else { _first.Push(false); _w.Write(','); }
        }

        // Un valor con nombre: la coma se escribe antes del nombre; el valor no vuelve a escribirla.
        private void Key(string name) { Sep(); _w.Write(Quote(name)); _w.Write(':'); }

        public void WriteStartObject() { Sep(); _w.Write('{'); _first.Push(true); }
        public void WriteStartObject(string name) { Key(name); _w.Write('{'); _first.Push(true); }
        public void WriteEndObject() { _first.Pop(); _w.Write('}'); }
        public void WriteStartArray() { Sep(); _w.Write('['); _first.Push(true); }
        public void WriteStartArray(string name) { Key(name); _w.Write('['); _first.Push(true); }
        public void WriteEndArray() { _first.Pop(); _w.Write(']'); }

        public void WriteString(string name, string v) { Key(name); _w.Write(Quote(v)); }
        public void WriteBoolean(string name, bool v) { Key(name); _w.Write(v ? "true" : "false"); }
        public void WriteNumber(string name, double v) { Key(name); _w.Write(Num(v)); }
        public void WriteNumber(string name, long v) { Key(name); _w.Write(v.ToString(CultureInfo.InvariantCulture)); }
        public void WriteNumberValue(double v) { Sep(); _w.Write(Num(v)); }
        public void WriteNumberValue(long v) { Sep(); _w.Write(v.ToString(CultureInfo.InvariantCulture)); }

        private static string Num(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Serializa un grafo simple: Dictionary&lt;string,object&gt;, IEnumerable, string, bool y numeros.</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Append(sb, value);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append(Quote(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: sb.Append(Num(d)); break;
                case float f: sb.Append(Num(f)); break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(Quote(kv.Key)).Append(':');
                        Append(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    var firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Append(sb, item);
                    }
                    sb.Append(']');
                    break;
                case IFormattable n: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
                default: sb.Append(Quote(v.ToString())); break;
            }
        }
    }
}
