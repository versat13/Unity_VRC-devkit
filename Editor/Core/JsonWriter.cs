using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Versat.VrcDevKit
{
    /// <summary>Minimal dependency-free JSON writer. Output is compact (LLM friendly).</summary>
    public sealed class JsonWriter
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly Stack<bool> _first = new Stack<bool>();
        bool _afterName;

        public JsonWriter BeginObject() { Sep(); _sb.Append('{'); _first.Push(true); return this; }
        public JsonWriter EndObject() { _first.Pop(); _sb.Append('}'); return this; }
        public JsonWriter BeginArray() { Sep(); _sb.Append('['); _first.Push(true); return this; }
        public JsonWriter EndArray() { _first.Pop(); _sb.Append(']'); return this; }
        public JsonWriter Name(string n) { Sep(); Str(n); _sb.Append(':'); _afterName = true; return this; }
        public JsonWriter Value(string v) { Sep(); if (v == null) _sb.Append("null"); else Str(v); return this; }
        public JsonWriter Value(bool v) { Sep(); _sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter Value(int v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(long v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Value(double v)
        {
            Sep();
            if (double.IsNaN(v) || double.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(System.Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture));
            return this;
        }
        public JsonWriter Raw(string json) { Sep(); _sb.Append(json); return this; }
        public JsonWriter Prop(string n, string v) => Name(n).Value(v);
        public JsonWriter Prop(string n, bool v) => Name(n).Value(v);
        public JsonWriter Prop(string n, int v) => Name(n).Value(v);
        public JsonWriter Prop(string n, double v) => Name(n).Value(v);
        public JsonWriter StringArray(string n, IEnumerable<string> items)
        {
            Name(n).BeginArray();
            if (items != null) foreach (var s in items) Value(s);
            return EndArray();
        }

        void Sep()
        {
            if (_afterName) { _afterName = false; return; }
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _sb.Append(',');
        }

        void Str(string s)
        {
            _sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() => _sb.ToString();

        public static string Error(string message)
            => new JsonWriter().BeginObject().Prop("ok", false).Prop("error", message).EndObject().ToString();
    }
}