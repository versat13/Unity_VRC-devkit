using System.Collections.Generic;

namespace Versat.VrcDevKit
{
    public enum Severity { Info = 0, Warning = 1, Error = 2 }

    /// <summary>One lint result. <see cref="Id"/> is stable across runs so it can be suppressed or referenced.</summary>
    public sealed class Finding
    {
        public string RuleId;
        public Severity Severity;
        public string Title;
        public string Message;
        public string Fix;
        public string Playable;   // FX / Action / ...
        public string Layer;
        public string State;      // state path, "AnyState", "Entry"
        public string ObjectPath; // hierarchy path relative to avatar root
        public string Param;
        public string Key;        // extra disambiguator for the id
        public List<string> Evidence = new List<string>();
        public string SuppressedReason;

        public string Id
        {
            get
            {
                var loc = Playable != null ? Playable + "/" + (Layer ?? "") : (ObjectPath ?? "");
                var id = RuleId + "|" + loc;
                if (!string.IsNullOrEmpty(State)) id += "|" + State;
                if (!string.IsNullOrEmpty(Param)) id += "|param=" + Param;
                if (!string.IsNullOrEmpty(Key)) id += "|" + Key;
                return id;
            }
        }

        public static string SevChar(Severity s) => s == Severity.Error ? "E" : s == Severity.Warning ? "W" : "I";

        public string OneLine() => "[" + SevChar(Severity) + "] " + RuleId + " " + Message + "  {id: " + Id + "}";

        public void Write(JsonWriter w)
        {
            w.BeginObject()
                .Prop("id", Id).Prop("rule", RuleId).Prop("severity", Severity.ToString().ToLowerInvariant())
                .Prop("title", Title).Prop("message", Message).Prop("fix", Fix);
            if (Playable != null) w.Prop("playable", Playable);
            if (Layer != null) w.Prop("layer", Layer);
            if (State != null) w.Prop("state", State);
            if (ObjectPath != null) w.Prop("objectPath", ObjectPath);
            if (Param != null) w.Prop("param", Param);
            if (Evidence.Count > 0) w.StringArray("evidence", Evidence);
            if (SuppressedReason != null) w.Prop("suppressed", SuppressedReason);
            w.EndObject();
        }
    }
}