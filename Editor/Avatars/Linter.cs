#if VRCDK_AVATARS
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Versat.VrcDevKit
{
    public static class Linter
    {
        public static List<Finding> Run(LintContext ctx, ICollection<string> ruleIds, List<string> errors)
        {
            ctx.Analyze();
            var results = new List<Finding>();
            foreach (var r in RuleRegistry.All)
            {
                if (ruleIds != null && ruleIds.Count > 0 && !ruleIds.Contains(r.Id)) continue;
                try { r.Run(ctx, results); }
                catch (System.Exception e) { errors?.Add(r.Id + ": " + e.GetType().Name + ": " + e.Message); }
            }
            var settings = DevKitSettings.Load();
            foreach (var f in results)
            {
                f.SuppressedReason = settings.SuppressReason(f.Id);
                if (f.SuppressedReason == null && (f.RuleId == "R001" || f.RuleId == "R002")) f.SuppressedReason = settings.OwnerOnlyReason(f.Playable, f.Layer);
            }
            return results.OrderByDescending(f => f.Severity).ThenBy(f => f.RuleId).ThenBy(f => f.Id).ToList();
        }

        public static string Markdown(LintContext ctx, List<Finding> findings, List<string> errors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Lint: " + ctx.AvatarName);
            sb.AppendLine();
            sb.AppendLine("- processed (NDMF/MA applied): " + ctx.Processed);
            sb.AppendLine("- controllers: " + string.Join(", ", ctx.Controllers.Select(c => c.Playable + "=" + c.Controller.name)));
            var active = findings.Where(f => f.SuppressedReason == null).ToList();
            sb.AppendLine("- findings: " + active.Count(f => f.Severity == Severity.Error) + " error / " + active.Count(f => f.Severity == Severity.Warning) + " warning / " + active.Count(f => f.Severity == Severity.Info) + " info, suppressed " + (findings.Count - active.Count));
            foreach (var n in ctx.Notes) sb.AppendLine("- note: " + n);
            foreach (var e in errors) sb.AppendLine("- RULE ERROR: " + e);
            foreach (var grp in findings.GroupBy(f => f.RuleId))
            {
                sb.AppendLine();
                sb.AppendLine("## " + grp.Key + " " + grp.First().Title + " (" + grp.Count() + ")");
                foreach (var f in grp)
                {
                    sb.AppendLine();
                    sb.AppendLine("- **[" + Finding.SevChar(f.Severity) + "]** " + f.Message + (f.SuppressedReason != null ? "  _(suppressed: " + f.SuppressedReason + ")_" : ""));
                    sb.AppendLine("  - id: `" + f.Id + "`");
                    sb.AppendLine("  - fix: " + f.Fix);
                    foreach (var ev in f.Evidence) sb.AppendLine("  - " + ev);
                }
            }
            return sb.ToString();
        }
    }
}
#endif