using System;
using System.IO;
using System.Text;

namespace Versat.VrcDevKit
{
    /// <summary>Writes reports under &lt;project&gt;/DevKitReports. Also keeps "&lt;kind&gt;_latest.*" copies for quick access.</summary>
    public static class ReportStore
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string Write(string kind, string subject, string json, string markdown)
        {
            var settings = DevKitSettings.Load();
            var rootDir = settings.reportRoot;
            var dir = Path.Combine(rootDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + kind + "_" + Sanitize(subject));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), json, Utf8);
            if (markdown != null) File.WriteAllText(Path.Combine(dir, "report.md"), markdown, Utf8);
            File.WriteAllText(Path.Combine(rootDir, kind + "_latest.json"), json, Utf8);
            if (markdown != null) File.WriteAllText(Path.Combine(rootDir, kind + "_latest.md"), markdown, Utf8);
            return dir.Replace('\\', '/');
        }

        /// <summary>kindOrPath: "lint" / "dump" (latest), a report folder, or a file path. Returns text truncated to maxChars.</summary>
        public static string Read(string kindOrPath, bool markdown, int maxChars, out string resolvedPath)
        {
            var root = DevKitSettings.Load().reportRoot;
            string path;
            if (File.Exists(kindOrPath)) path = kindOrPath;
            else if (Directory.Exists(kindOrPath)) path = Path.Combine(kindOrPath, markdown ? "report.md" : "report.json");
            else path = Path.Combine(root, kindOrPath + "_latest." + (markdown ? "md" : "json"));
            resolvedPath = path.Replace('\\', '/');
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (maxChars > 0 && text.Length > maxChars) text = text.Substring(0, maxChars) + "\n...[truncated " + (text.Length - maxChars) + " chars]";
            return text;
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "none";
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            var r = sb.ToString();
            return r.Length > 40 ? r.Substring(0, 40) : r;
        }
    }
}