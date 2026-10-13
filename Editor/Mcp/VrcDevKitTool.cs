#if VRCDK_MCP
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace Versat.VrcDevKit.Mcp
{
    /// <summary>Exposes <see cref="Versat.VrcDevKit.Api"/> as the MCP tool "vrc_devkit".</summary>
    [McpForUnityTool("vrc_devkit", Description =
        "VRChat gimmick dev kit (read-only analysis). actions: help, status, rules, lint, dump, read_report, suppress, unsuppress, mark_owner_only_layer, unmark_owner_only_layer, exclude_avatar, select, self_test. " +
        "lint finds sync/animator bugs (remote reads of local-only params, owner-only PhysBone/Contact values, stuck transitions, split transform axes, blend tree clip name clashes, driver float->int truncation) " +
        "on a temporary NDMF/MA-processed clone. Results are summarized inline and saved to DevKitReports/. Start with action=help.")]
    public static class VrcDevKitTool
    {
        public class Parameters
        {
            [ToolParameter("help | status | rules | lint | dump | read_report | suppress | unsuppress | mark_owner_only_layer | unmark_owner_only_layer | exclude_avatar | select | self_test")]
            public string action { get; set; }

            [ToolParameter("Avatar GameObject name or hierarchy path (lint/dump/select). Empty = selected avatar.", Required = false)]
            public string avatar { get; set; }

            [ToolParameter("Options JSON. lint: {ndmf,rules,minSeverity,inlineMax,includeSuppressed}. dump: {ndmf,sections,layerFilter,pathFilter,transitions,behaviours}", Required = false)]
            public string options { get; set; }

            [ToolParameter("read_report: 'lint'|'dump'|folder|file. suppress/unsuppress: finding id. mark/unmark_owner_only_layer: regex on 'Playable/Layer'. exclude_avatar: avatar name. select: object path.", Required = false)]
            public string target { get; set; }

            [ToolParameter("suppress/mark_owner_only_layer: reason. read_report: 'md' or 'json'. exclude_avatar: 'false' to un-exclude.", Required = false)]
            public string value { get; set; }

            [ToolParameter("read_report: max characters (default 20000)", Required = false)]
            public int max_chars { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            string S(string k) { var t = p[k]; return t == null || t.Type == JTokenType.Null ? null : (t.Type == JTokenType.Object || t.Type == JTokenType.Array ? t.ToString(Newtonsoft.Json.Formatting.None) : t.ToString()); }
            var action = (S("action") ?? "help").ToLowerInvariant();
            var max = p["max_chars"] != null && p["max_chars"].Type == JTokenType.Integer ? (int)p["max_chars"] : 20000;
            string json;
            switch (action)
            {
                case "help": json = Api.Help(); break;
                case "status": case "list_avatars": json = Api.Status(); break;
                case "rules": json = Api.Rules(); break;
                case "lint": json = Api.Lint(S("avatar"), S("options")); break;
                case "dump": json = Api.Dump(S("avatar"), S("options")); break;
                case "read_report": json = Api.ReadReport(S("target") ?? "lint", S("value") ?? "md", max); break;
                case "suppress": json = Api.Suppress(S("target"), S("value")); break;
                case "unsuppress": json = Api.Unsuppress(S("target")); break;
                case "mark_owner_only_layer": json = Api.MarkOwnerOnlyLayer(S("target"), S("value"), true); break;
                case "unmark_owner_only_layer": json = Api.MarkOwnerOnlyLayer(S("target"), null, false); break;
                case "exclude_avatar": json = Api.ExcludeAvatar(S("target") ?? S("avatar"), S("value") != "false"); break;
                case "select": json = Api.Select(S("avatar"), S("target")); break;
                case "self_test": json = Api.RunSelfTest(); break;
                default: return new ErrorResponse("unknown action '" + action + "'. use action=help");
            }
            var tok = JToken.Parse(json);
            var ok = tok["ok"] == null || tok["ok"].Type != JTokenType.Boolean || (bool)tok["ok"];
            return ok ? (object)new SuccessResponse("vrc_devkit " + action, tok) : new ErrorResponse((string)tok["error"] ?? "error", tok);
        }
    }
}
#endif