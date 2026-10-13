#if VRCDK_AVATARS
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

namespace Versat.VrcDevKit
{
    [System.Serializable]
    public sealed class DumpOptions
    {
        public bool ndmf = true;
        /// <summary>comma separated: params,layers,constraints,contacts,physbones</summary>
        public string sections = "params,layers,constraints,contacts,physbones";
        /// <summary>regex on "Playable/LayerName"; empty = all</summary>
        public string layerFilter = "";
        public bool transitions = true;
        public bool behaviours = true;
        /// <summary>regex on hierarchy path for constraints/contacts/physbones; empty = all</summary>
        public string pathFilter = "";
    }

    public static class Dumper
    {
        public static string Json(LintContext ctx, DumpOptions o, out string markdown)
        {
            ctx.Analyze();
            var sec = (o.sections ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).ToList();
            var lf = string.IsNullOrEmpty(o.layerFilter) ? null : new Regex(o.layerFilter, RegexOptions.IgnoreCase);
            var pf = string.IsNullOrEmpty(o.pathFilter) ? null : new Regex(o.pathFilter, RegexOptions.IgnoreCase);
            var md = new StringBuilder();
            md.AppendLine("# Dump: " + ctx.AvatarName + " (processed=" + ctx.Processed + ")");

            var w = new JsonWriter().BeginObject();
            w.Prop("ok", true).Prop("avatar", ctx.AvatarName).Prop("processed", ctx.Processed).StringArray("notes", ctx.Notes);

            if (sec.Contains("params"))
            {
                var synced = ctx.Expr.Values.Where(e => e.Synced).OrderBy(e => e.Name).ToList();
                int bits = synced.Sum(e => e.Bits);
                w.Name("params").BeginObject().Prop("syncedBits", bits).Prop("budget", 256);
                w.Name("synced").BeginArray();
                foreach (var e in synced)
                    w.BeginObject().Prop("name", e.Name).Prop("type", e.Type).Prop("bits", e.Bits).Prop("saved", e.Saved).Prop("default", e.Default).EndObject();
                w.EndArray();
                w.StringArray("localExpression", ctx.Expr.Values.Where(e => !e.Synced).Select(e => e.Name + ":" + e.Type).OrderBy(s => s));
                w.StringArray("animatorOnly", ctx.ParamTypes.Where(kv => !ctx.Expr.ContainsKey(kv.Key) && !LintContext.Builtins.Contains(kv.Key)).Select(kv => kv.Key + ":" + kv.Value).OrderBy(s => s));
                w.EndObject();
                md.AppendLine("- synced bits: " + bits + "/256 (" + synced.Count + " params)");
            }

            if (sec.Contains("layers"))
            {
                w.Name("layers").BeginArray();
                int shown = 0;
                foreach (var li in ctx.Layers)
                {
                    var key = li.Playable + "/" + li.Layer.name;
                    if (lf != null && !lf.IsMatch(key)) continue;
                    shown++;
                    var g = li.Graph;
                    w.BeginObject().Prop("playable", li.Playable).Prop("index", li.Index).Prop("name", li.Layer.name)
                        .Prop("weight", li.Layer.defaultWeight).Prop("synced", li.IsSynced)
                        .Prop("defaultState", g.Root != null && g.Root.defaultState != null ? g.PathOf(g.Root.defaultState) : null)
                        .Prop("stateCount", g.StatePath.Count).Prop("remoteReachable", li.RemoteReachable.Count);
                    w.Name("states").BeginArray();
                    foreach (var kv in g.StatePath)
                    {
                        var s = kv.Key;
                        w.BeginObject().Prop("path", kv.Value).Prop("motion", SmGraph.MotionLabel(li.MotionOf(s))).Prop("remote", li.RemoteReachable.Contains(s));
                        if (s.speedParameterActive) w.Prop("speedParam", s.speedParameter);
                        if (s.timeParameterActive) w.Prop("timeParam", s.timeParameter);
                        if (!s.writeDefaultValues) { } else w.Prop("writeDefaults", true);
                        if (o.behaviours)
                        {
                            var bs = li.BehavioursOf(s);
                            if (bs != null && bs.Length > 0)
                            {
                                w.Name("behaviours").BeginArray();
                                foreach (var b in bs)
                                {
                                    if (b is VRCAvatarParameterDriver d)
                                        w.Value("Driver" + (d.localOnly ? "(localOnly)" : "") + ": " + string.Join("; ", d.parameters.Select(p =>
                                            p.type + " " + p.name + (p.type.ToString() == "Copy" ? " <- " + p.source + (p.convertRange ? " [" + p.sourceMin + "," + p.sourceMax + "]->[" + p.destMin + "," + p.destMax + "]" : "") : p.type.ToString() == "Random" ? " [" + p.valueMin + "," + p.valueMax + "]" : " = " + p.value))));
                                    else if (b != null) w.Value(b.GetType().Name);
                                }
                                w.EndArray();
                            }
                        }
                        if (o.transitions && !li.IsSynced && s.transitions.Length > 0)
                        {
                            w.Name("transitions").BeginArray();
                            foreach (var t in s.transitions)
                            {
                                if (t == null) continue;
                                var dest = t.destinationState != null ? g.PathOf(t.destinationState) : t.isExit ? "Exit" : t.destinationStateMachine != null ? t.destinationStateMachine.name + "/" : "?";
                                var cond = string.Join(" && ", t.conditions.Select(SmGraph.ConditionText));
                                w.Value("-> " + dest + (cond.Length > 0 ? " | " + cond : "") + (t.hasExitTime ? " | exit=" + t.exitTime : "") + (t.duration > 0 ? " | dur=" + t.duration : ""));
                            }
                            w.EndArray();
                        }
                        w.EndObject();
                    }
                    w.EndArray();
                    if (o.transitions && !li.IsSynced)
                    {
                        w.Name("anyState").BeginArray();
                        foreach (var sm in g.Machines)
                            foreach (var t in sm.anyStateTransitions)
                            {
                                if (t == null) continue;
                                var cond = string.Join(" && ", t.conditions.Select(SmGraph.ConditionText));
                                w.Value(g.MachinePath[sm] + "Any -> " + (t.destinationState != null ? g.PathOf(t.destinationState) : "?") + (cond.Length > 0 ? " | " + cond : ""));
                            }
                        w.EndArray();
                    }
                    w.EndObject();
                }
                w.EndArray();
                md.AppendLine("- layers: " + shown + " shown / " + ctx.Layers.Count + " total");
            }

            if (sec.Contains("constraints") && ctx.Root != null)
            {
                w.Name("constraints").BeginArray();
                int n = 0;
                foreach (var c in ctx.Root.GetComponentsInChildren<VRCConstraintBase>(true))
                {
                    var path = ctx.RelPath(c.transform);
                    if (pf != null && !pf.IsMatch(path)) continue;
                    n++;
                    w.BeginObject().Prop("path", path).Prop("type", c.GetType().Name).Prop("active", c.IsActive).Prop("enabled", c.enabled)
                        .Prop("weight", c.GlobalWeight).Prop("freezeToWorld", c.FreezeToWorld).Prop("localSpace", c.SolveInLocalSpace);
                    if (c.TargetTransform != null) w.Prop("target", ctx.RelPath(c.TargetTransform));
                    w.Name("sources").BeginArray();
                    for (int i = 0; i < c.Sources.Count; i++)
                    {
                        var src = c.Sources[i];
                        var st = src.SourceTransform;
                        string sp = st == null ? null : (st.IsChildOf(ctx.Root) ? ctx.RelPath(st) : (st.gameObject.scene.IsValid() ? "scene:" + AvatarSession.HierarchyPath(st) : "asset:" + st.name));
                        w.Value(sp + " w=" + src.Weight);
                    }
                    w.EndArray();
                    if (c is VRC.Dynamics.ManagedTypes.VRCPositionConstraintBase pc) w.Prop("axes", (pc.AffectsPositionX ? "X" : "") + (pc.AffectsPositionY ? "Y" : "") + (pc.AffectsPositionZ ? "Z" : ""));
                    w.EndObject();
                }
                foreach (var c in ctx.Root.GetComponentsInChildren<UnityEngine.Animations.IConstraint>(true))
                {
                    var comp = c as Component;
                    var path = ctx.RelPath(comp.transform);
                    if (pf != null && !pf.IsMatch(path)) continue;
                    n++;
                    w.BeginObject().Prop("path", path).Prop("type", comp.GetType().Name + "(Unity)").Prop("active", c.constraintActive).Prop("weight", c.weight).EndObject();
                }
                w.EndArray();
                md.AppendLine("- constraints: " + n);
            }

            if (sec.Contains("contacts") && ctx.Root != null)
            {
                w.Name("contacts").BeginArray();
                int n = 0;
                foreach (var c in ctx.Root.GetComponentsInChildren<ContactBase>(true))
                {
                    var path = ctx.RelPath(c.transform);
                    if (pf != null && !pf.IsMatch(path)) continue;
                    n++;
                    var ls = c.transform.lossyScale;
                    float maxS = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                    float minS = Mathf.Min(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                    w.BeginObject().Prop("path", path).Prop("kind", c is ContactReceiver ? "receiver" : "sender").Prop("shape", c.shapeType.ToString())
                        .Prop("radius", c.radius).Prop("effectiveRadius", c.radius * maxS).Prop("nonUniformScale", maxS - minS > 1e-4f * Mathf.Max(1f, maxS))
                        .Prop("localOnly", c.localOnly).StringArray("tags", c.collisionTags);
                    if (c is ContactReceiver r) w.Prop("param", r.parameter).Prop("receiverType", r.receiverType.ToString()).Prop("allowSelf", r.allowSelf).Prop("allowOthers", r.allowOthers);
                    w.EndObject();
                }
                w.EndArray();
                md.AppendLine("- contacts: " + n);
            }

            if (sec.Contains("physbones") && ctx.Root != null)
            {
                w.Name("physbones").BeginArray();
                int n = 0;
                foreach (var pb in ctx.Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                {
                    var path = ctx.RelPath(pb.transform);
                    if (pf != null && !pf.IsMatch(path)) continue;
                    n++;
                    w.BeginObject().Prop("path", path).Prop("param", pb.parameter).Prop("grab", pb.allowGrabbing.ToString()).Prop("pose", pb.allowPosing.ToString())
                        .Prop("root", pb.rootTransform != null ? ctx.RelPath(pb.rootTransform) : null).EndObject();
                }
                w.EndArray();
                md.AppendLine("- physbones: " + n);
            }

            w.EndObject();
            markdown = md.ToString();
            return w.ToString();
        }
    }
}
#endif