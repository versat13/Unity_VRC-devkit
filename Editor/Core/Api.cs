using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Versat.VrcDevKit
{
    [Serializable]
    public sealed class LintOptions
    {
        /// <summary>Apply NDMF/MA to a temporary clone first (recommended).</summary>
        public bool ndmf = true;
        /// <summary>Comma separated rule ids, empty = all.</summary>
        public string rules = "";
        /// <summary>info / warning / error</summary>
        public string minSeverity = "info";
        /// <summary>Max findings returned inline (full list is always in the report file).</summary>
        public int inlineMax = 25;
        public bool includeSuppressed = false;
    }

    /// <summary>
    /// LLM-facing entry points. Every method takes strings and returns a compact JSON string with "ok".
    /// Call from unityMCP execute_code:  return Versat.VrcDevKit.Api.Lint("AvatarName");
    /// or through the MCP tool "vrc_devkit" (action = method name in snake_case).
    /// Read-only: nothing here modifies avatars, controllers or scenes (analysis uses a temporary clone).
    /// </summary>
    public static class Api
    {
        public const string Version = "0.1.0";

        static string Guard(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return JsonWriter.Error(e.GetType().Name + ": " + e.Message); }
        }

        static T Parse<T>(string json) where T : new()
        {
            var o = new T();
            if (!string.IsNullOrWhiteSpace(json)) JsonUtility.FromJsonOverwrite(json, o);
            return o;
        }

        public static string Help() => Guard(() =>
        {
            var w = new JsonWriter().BeginObject().Prop("ok", true).Prop("version", Version);
            w.Name("actions").BeginArray()
                .Value("status() -> environment, installed features, avatars in scene, exclusions")
                .Value("list_avatars() -> avatar names/paths")
                .Value("rules() -> lint rule ids, titles, descriptions")
                .Value("lint(avatar, options?) options={ndmf:true, rules:'R001,R003', minSeverity:'warning', inlineMax:25, includeSuppressed:false}")
                .Value("dump(avatar, options?) options={ndmf:true, sections:'params,layers,constraints,contacts,physbones', layerFilter:'regex on Playable/Layer', pathFilter:'regex', transitions:true, behaviours:true}")
                .Value("read_report(target='lint'|'dump'|folder|file, format='md'|'json', maxChars=20000)")
                .Value("suppress(findingId, reason) / unsuppress(findingId)")
                .Value("mark_owner_only_layer(pattern=regex on 'Playable/Layer', reason, mark=true) -> R001/R002 in those layers are suppressed (layer effects only visible to the owner)")
                .Value("exclude_avatar(name, exclude=true) -> DevKit will never clone/process/analyse it")
                .Value("select(avatar, objectPath) -> selects/pings an object in the SOURCE avatar for the human")
                .Value("self_test() -> regression cases for every rule")
                .EndArray();
            w.Name("workflow").BeginArray()
                .Value("1. status  2. lint(avatar)  3. read_report('lint') if more detail is needed")
                .Value("4. fix the controller/prefab  5. lint again; suppress only intentional findings with a reason")
                .Value("dump with layerFilter/pathFilter to inspect structure without opening Unity UI")
                .EndArray();
            w.Prop("reports", DevKitSettings.Load().reportRoot + "/<timestamp>_<kind>_<avatar>/report.{json,md} and <kind>_latest.{json,md}");
            return w.EndObject().ToString();
        });

        public static string Status() => Guard(() =>
        {
            var s = DevKitSettings.Load();
            var w = new JsonWriter().BeginObject().Prop("ok", true).Prop("version", Version)
                .Prop("unity", Application.unityVersion).Prop("playing", EditorApplication.isPlaying);
            w.Name("features").BeginObject()
#if VRCDK_AVATARS
                .Prop("avatars", true)
#else
                .Prop("avatars", false)
#endif
#if VRCDK_NDMF
                .Prop("ndmf", true)
#else
                .Prop("ndmf", false)
#endif
                .Prop("mcpTool", Type.GetType("Versat.VrcDevKit.Mcp.VrcDevKitTool, Versat.VrcDevKit.Mcp") != null)
                .Prop("av3emulator", Type.GetType("Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime, lyuma.av3emulator") != null)
                .EndObject();
            w.StringArray("excludedAvatars", s.excludedAvatars).Prop("suppressions", s.suppressions.Count)
                .Prop("settings", DevKitSettings.FilePath).Prop("reportRoot", s.reportRoot);
#if VRCDK_AVATARS
            w.Name("avatars").BeginArray();
            foreach (var a in AvatarSession.FindAll())
                w.BeginObject().Prop("name", a.gameObject.name).Prop("path", AvatarSession.HierarchyPath(a.transform))
                    .Prop("activeInHierarchy", a.gameObject.activeInHierarchy).Prop("excluded", s.IsExcluded(a.gameObject.name)).EndObject();
            w.EndArray();
#endif
            return w.EndObject().ToString();
        });

        public static string ListAvatars() => Status();

        public static string Rules() => Guard(() =>
        {
#if VRCDK_AVATARS
            var w = new JsonWriter().BeginObject().Prop("ok", true).Name("rules").BeginArray();
            foreach (var r in RuleRegistry.All) w.BeginObject().Prop("id", r.Id).Prop("title", r.Title).Prop("description", r.Description).EndObject();
            return w.EndArray().EndObject().ToString();
#else
            return JsonWriter.Error("VRChat Avatars SDK not installed");
#endif
        });

        // Last lint results for the editor window.
        public static List<Finding> LastFindings = new List<Finding>();
        public static string LastAvatar;

        public static string Lint(string avatar, string optionsJson = null) => Guard(() =>
        {
#if VRCDK_AVATARS
            var o = Parse<LintOptions>(optionsJson);
            var desc = AvatarSession.Find(avatar, out var err);
            if (desc == null) return JsonWriter.Error(err);
            var minSev = o.minSeverity == "error" ? Severity.Error : o.minSeverity == "warning" ? Severity.Warning : Severity.Info;
            var ruleIds = (o.rules ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            List<Finding> fs; LintContext ctx; var errs = new List<string>(); string dir;
            using (var s = AvatarSession.Open(desc, o.ndmf))
            {
                // Everything touching Unity objects must happen before Dispose (NDMF temp assets are destroyed there).
                ctx = s.BuildContext();
                fs = Linter.Run(ctx, ruleIds, errs);
                fs = fs.Where(f => f.Severity >= minSev).ToList();
                var full = new JsonWriter().BeginObject().Prop("ok", true).Prop("kind", "lint").Prop("avatar", ctx.AvatarName).Prop("processed", ctx.Processed)
                    .StringArray("notes", ctx.Notes).StringArray("ruleErrors", errs)
                    .StringArray("controllers", ctx.Controllers.Select(c => c.Playable + "=" + c.Controller.name));
                full.Name("findings").BeginArray(); foreach (var f in fs) f.Write(full); full.EndArray().EndObject();
                dir = ReportStore.Write("lint", ctx.AvatarName, full.ToString(), Linter.Markdown(ctx, fs, errs));
            }
            LastFindings = fs; LastAvatar = desc.gameObject.name;
            var active = fs.Where(f => f.SuppressedReason == null).ToList();

            var w = new JsonWriter().BeginObject().Prop("ok", true).Prop("avatar", ctx.AvatarName).Prop("processed", ctx.Processed).Prop("reportDir", dir);
            w.Name("counts").BeginObject()
                .Prop("error", active.Count(f => f.Severity == Severity.Error))
                .Prop("warning", active.Count(f => f.Severity == Severity.Warning))
                .Prop("info", active.Count(f => f.Severity == Severity.Info))
                .Prop("suppressed", fs.Count - active.Count).EndObject();
            w.StringArray("notes", ctx.Notes).StringArray("ruleErrors", errs);
            var inline = (o.includeSuppressed ? fs : active).Take(Math.Max(0, o.inlineMax)).ToList();
            w.StringArray("findings", inline.Select(f => f.OneLine()));
            int rest = (o.includeSuppressed ? fs.Count : active.Count) - inline.Count;
            if (rest > 0) w.Prop("more", rest + " more in report (read_report('lint'))");
            return w.EndObject().ToString();
#else
            return JsonWriter.Error("VRChat Avatars SDK not installed");
#endif
        });

        public static string Dump(string avatar, string optionsJson = null) => Guard(() =>
        {
#if VRCDK_AVATARS
            var o = Parse<DumpOptions>(optionsJson);
            var desc = AvatarSession.Find(avatar, out var err);
            if (desc == null) return JsonWriter.Error(err);
            string json, md;
            using (var s = AvatarSession.Open(desc, o.ndmf))
            {
                var ctx = s.BuildContext();
                json = Dumper.Json(ctx, o, out md);
            }
            var dir = ReportStore.Write("dump", desc.gameObject.name, json, md);
            var w = new JsonWriter().BeginObject().Prop("ok", true).Prop("reportDir", dir).Prop("chars", json.Length).Prop("summary", md);
            if (json.Length <= 12000) w.Name("dump").Raw(json);
            else w.Prop("hint", "dump is large; read_report('dump', 'json') or narrow with layerFilter/pathFilter/sections");
            return w.EndObject().ToString();
#else
            return JsonWriter.Error("VRChat Avatars SDK not installed");
#endif
        });

        public static string ReadReport(string target = "lint", string format = "md", int maxChars = 20000) => Guard(() =>
        {
            var text = ReportStore.Read(string.IsNullOrEmpty(target) ? "lint" : target, format != "json", maxChars, out var path);
            if (text == null) return JsonWriter.Error("report not found: " + path);
            return new JsonWriter().BeginObject().Prop("ok", true).Prop("path", path).Prop("content", text).EndObject().ToString();
        });

        public static string Suppress(string findingId, string reason) => Guard(() =>
        {
            if (string.IsNullOrEmpty(findingId)) return JsonWriter.Error("findingId required");
            var s = DevKitSettings.Load();
            s.suppressions.RemoveAll(x => x.id == findingId);
            s.suppressions.Add(new DevKitSettings.Suppression { id = findingId, reason = reason ?? "" });
            s.Save();
            return new JsonWriter().BeginObject().Prop("ok", true).Prop("suppressed", findingId).Prop("total", s.suppressions.Count).EndObject().ToString();
        });

        public static string Unsuppress(string findingId) => Guard(() =>
        {
            var s = DevKitSettings.Load();
            int n = s.suppressions.RemoveAll(x => x.id == findingId);
            s.Save();
            return new JsonWriter().BeginObject().Prop("ok", true).Prop("removed", n).EndObject().ToString();
        });

        /// <summary>Declare layers (regex on "Playable/Layer") whose effects only the owner can see. R001/R002 there become suppressed.</summary>
        public static string MarkOwnerOnlyLayer(string pattern, string reason, bool mark = true) => Guard(() =>
        {
            if (string.IsNullOrEmpty(pattern)) return JsonWriter.Error("pattern required");
            try { System.Text.RegularExpressions.Regex.IsMatch("", pattern); } catch (ArgumentException e) { return JsonWriter.Error("bad regex: " + e.Message); }
            var s = DevKitSettings.Load();
            s.ownerOnlyLayers.RemoveAll(x => x.pattern == pattern);
            if (mark) s.ownerOnlyLayers.Add(new DevKitSettings.OwnerOnlyLayer { pattern = pattern, reason = reason ?? "" });
            s.Save();
            var w = new JsonWriter().BeginObject().Prop("ok", true).Name("ownerOnlyLayers").BeginArray();
            foreach (var o in s.ownerOnlyLayers) w.Value(o.pattern + "  // " + o.reason);
            return w.EndArray().EndObject().ToString();
        });

        public static string ExcludeAvatar(string name, bool exclude = true) => Guard(() =>
        {
            if (string.IsNullOrEmpty(name)) return JsonWriter.Error("name required");
            var s = DevKitSettings.Load();
            s.excludedAvatars.Remove(name);
            if (exclude) s.excludedAvatars.Add(name);
            s.Save();
            return new JsonWriter().BeginObject().Prop("ok", true).StringArray("excludedAvatars", s.excludedAvatars).EndObject().ToString();
        });

        public static string Select(string avatar, string objectPath) => Guard(() =>
        {
#if VRCDK_AVATARS
            var desc = AvatarSession.Find(avatar, out var err);
            if (desc == null) return JsonWriter.Error(err);
            var t = string.IsNullOrEmpty(objectPath) ? desc.transform : desc.transform.Find(objectPath);
            if (t == null) return JsonWriter.Error("object not found under avatar: " + objectPath + " (it may only exist after NDMF/MA processing)");
            Selection.activeGameObject = t.gameObject;
            EditorGUIUtility.PingObject(t.gameObject);
            return new JsonWriter().BeginObject().Prop("ok", true).Prop("selected", AvatarSession.HierarchyPath(t)).EndObject().ToString();
#else
            return JsonWriter.Error("VRChat Avatars SDK not installed");
#endif
        });

        public static string RunSelfTest() => Guard(() =>
        {
#if VRCDK_AVATARS
            return SelfTest.Run();
#else
            return JsonWriter.Error("VRChat Avatars SDK not installed");
#endif
        });
    }
}