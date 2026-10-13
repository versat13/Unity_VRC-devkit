#if VRCDK_AVATARS
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;

namespace Versat.VrcDevKit
{
    public interface ILintRule
    {
        string Id { get; }
        string Title { get; }
        /// <summary>What the rule detects and why it matters (shown by Api.Rules()).</summary>
        string Description { get; }
        void Run(LintContext ctx, List<Finding> results);
    }

    public static class RuleRegistry
    {
        public static readonly List<ILintRule> All = new List<ILintRule>
        {
            new R001_RemoteReadsLocalParam(),
            new R002_RemoteReadsOwnerOnlyDynamics(),
            new R003_StuckTransition(),
            new R004_SplitTransformAxes(),
            new R005_BlendTreeClipNameClash(),
            new R006_DriverFloatToIntTruncation(),
        };
    }

    static class RuleUtil
    {
        public static IEnumerable<IGrouping<(string, string, string), ParamUse>> RemoteReadsByLayerParam(LintContext ctx)
            => ctx.Uses.Where(u => u.Remote).GroupBy(u => (u.Playable, u.Layer, u.Param));

        public static void AddEvidence(Finding f, IEnumerable<ParamUse> uses, int max = 6)
        {
            var list = uses.Select(u => u.State + ": " + u.Usage).Distinct().ToList();
            foreach (var s in list.Take(max)) f.Evidence.Add("read @ " + s);
            if (list.Count > max) f.Evidence.Add("... +" + (list.Count - max) + " more reads");
        }
    }

    // ------------------------------------------------------------------
    sealed class R001_RemoteReadsLocalParam : ILintRule
    {
        public string Id => "R001";
        public string Title => "リモートで変化しないパラメータをリモート経路で参照";
        public string Description =>
            "IsLocal で分岐されていない（リモートでも動く）ステート・遷移・BT・Driver Copy が、" +
            "ローカルでしか書かれないパラメータ（非同期のExpression、localOnly Driver、ローカル専用ステートのDriver）を読んでいる。" +
            "リモートでは値が既定値のままなので見た目や動作がずれる（例: 撮影設定が未同期でリモートの見た目がずれた件）。";

        public void Run(LintContext ctx, List<Finding> results)
        {
            var localWriters = ctx.Writers.Where(w => !w.Remote).GroupBy(w => w.Param).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var g in RuleUtil.RemoteReadsByLayerParam(ctx))
            {
                var p = g.Key.Item3;
                if (ctx.RemoteAvailable.Contains(p)) continue;
                if (ctx.PhysBoneParams.ContainsKey(p)) continue; // R002
                if (ctx.ContactParams.TryGetValue(p, out var cp) && cp.LocalOnly) continue; // R002
                if (!localWriters.TryGetValue(p, out var ws)) continue; // never written -> constant, harmless
                var f = new Finding
                {
                    RuleId = Id, Severity = Severity.Warning, Title = Title,
                    Playable = g.Key.Item1, Layer = g.Key.Item2, Param = p,
                    Message = "「" + p + "」はリモートでは変化しないのに、" + g.Key.Item1 + "/" + g.Key.Item2 + " のリモートでも動く経路で読まれています（" + g.Count() + "箇所）",
                    Fix = "(a) 同期パラメータにする (b) 同期済みの別パラメータへ Driver Copy して読む側を差し替える (c) この経路を IsLocal で分岐してローカル専用にする",
                };
                RuleUtil.AddEvidence(f, g);
                foreach (var w in ws.Take(3)) f.Evidence.Add("written by " + w.Kind + " @ " + w.Where);
                results.Add(f);
            }
        }
    }

    // ------------------------------------------------------------------
    sealed class R002_RemoteReadsOwnerOnlyDynamics : ILintRule
    {
        public string Id => "R002";
        public string Title => "所有者でしか正確でない PhysBone/Contact 値をリモート経路で参照";
        public string Description =>
            "PhysBone の _IsGrabbed/_IsPosed は掴んだ本人の端末でしか正確でない。localOnly の Contact 受信機はリモートでは動かない。" +
            "これらをリモートでも動く経路で読むと、他人から見た状態がずれる（例: ペン/ボードの掴み状態を SyncGrab で同期コピーした件）。";

        public void Run(LintContext ctx, List<Finding> results)
        {
            foreach (var g in RuleUtil.RemoteReadsByLayerParam(ctx))
            {
                var p = g.Key.Item3;
                Finding f = null;
                if (ctx.PhysBoneParams.TryGetValue(p, out var pbPath) && (p.EndsWith("_IsGrabbed") || p.EndsWith("_IsPosed")))
                {
                    f = new Finding
                    {
                        RuleId = Id, Severity = Severity.Warning, Title = Title, ObjectPath = pbPath,
                        Message = "PhysBone の「" + p + "」は掴んだ本人以外では不正確ですが、" + g.Key.Item1 + "/" + g.Key.Item2 + " のリモート経路で読まれています",
                        Fix = "オーナー側（IsLocal）で同期パラメータへ Driver Copy し、リモート経路はそのコピーを読む",
                    };
                }
                else if (ctx.ContactParams.TryGetValue(p, out var cp) && cp.LocalOnly)
                {
                    f = new Finding
                    {
                        RuleId = Id, Severity = Severity.Error, Title = Title, ObjectPath = cp.Path,
                        Message = "localOnly の Contact 値「" + p + "」はリモートでは常に既定値ですが、" + g.Key.Item1 + "/" + g.Key.Item2 + " のリモート経路で読まれています",
                        Fix = "読む経路を IsLocal で分岐する／結果を同期パラメータへコピーしてリモートはそれを読む",
                    };
                }
                if (f == null) continue;
                f.Playable = g.Key.Item1; f.Layer = g.Key.Item2; f.Param = p;
                RuleUtil.AddEvidence(f, g);
                results.Add(f);
            }
        }
    }

    // ------------------------------------------------------------------
    sealed class R003_StuckTransition : ILintRule
    {
        public string Id => "R003";
        public string Title => "条件も ExitTime もない遷移（永久に発火しない）／無条件の AnyState 遷移";
        public string Description =>
            "条件なし・Has Exit Time オフの遷移は発火せず、ステートが止まる（例: シャッターの Review→WaitRelease で初回以降撮れなくなった件）。" +
            "条件なしの AnyState 遷移は毎フレーム発火し続ける。";

        public void Run(LintContext ctx, List<Finding> results)
        {
            foreach (var li in ctx.Layers)
            {
                if (li.IsSynced) continue;
                var g = li.Graph;
                foreach (var kv in g.StatePath)
                    foreach (var t in kv.Key.transitions)
                    {
                        if (t == null || t.conditions.Length > 0 || t.hasExitTime) continue;
                        var dest = t.destinationState != null ? g.PathOf(t.destinationState) : t.isExit ? "Exit" : t.destinationStateMachine != null ? t.destinationStateMachine.name + "/" : "?";
                        results.Add(new Finding
                        {
                            RuleId = Id, Severity = Severity.Error, Title = Title,
                            Playable = li.Playable, Layer = li.Layer.name, State = kv.Value, Key = "->" + dest,
                            Message = li.Playable + "/" + li.Layer.name + " の " + kv.Value + " → " + dest + " は条件も ExitTime もなく、永久に遷移しません",
                            Fix = "Has Exit Time をオンにして Exit Time 0 にする、または条件を付ける",
                        });
                    }
                foreach (var sm in g.Machines)
                    foreach (var t in sm.anyStateTransitions)
                    {
                        if (t == null || t.conditions.Length > 0) continue;
                        var dest = t.destinationState != null ? g.PathOf(t.destinationState) : "?";
                        results.Add(new Finding
                        {
                            RuleId = Id, Severity = Severity.Warning, Title = Title,
                            Playable = li.Playable, Layer = li.Layer.name, State = g.MachinePath[sm] + "AnyState", Key = "->" + dest,
                            Message = li.Playable + "/" + li.Layer.name + " の AnyState → " + dest + " に条件がなく、毎フレーム発火します",
                            Fix = "条件を付ける（Can Transition To Self もオフに）",
                        });
                    }
            }
        }
    }

    // ------------------------------------------------------------------
    sealed class R004_SplitTransformAxes : ILintRule
    {
        public string Id => "R004";
        public string Title => "同じ Transform の位置/回転/スケールを複数レイヤーが別々の軸で書いている";
        public string Description =>
            "Animator は Transform の位置・回転・スケールをベクトル単位で書くため、レイヤーAが x、レイヤーBが y を書くと後のレイヤーが全軸を上書きする" +
            "（例: localPosition を軸別レイヤーで動かして片方が効かなかった件）。1レイヤーの 2D BlendTree にまとめる。";

        static string Group(string prop)
        {
            if (prop.StartsWith("m_LocalPosition")) return "position";
            if (prop.StartsWith("m_LocalRotation") || prop.StartsWith("localEulerAngles")) return "rotation";
            if (prop.StartsWith("m_LocalScale")) return "scale";
            return null;
        }

        public void Run(LintContext ctx, List<Finding> results)
        {
            foreach (var byCtrl in ctx.Layers.GroupBy(l => l.Controller))
            {
                // key: path|group -> layerName -> components
                var map = new Dictionary<string, Dictionary<string, SortedSet<string>>>();
                foreach (var li in byCtrl)
                    foreach (var s in li.Graph.StatePath.Keys)
                        foreach (var clip in SmGraph.Clips(li.MotionOf(s)))
                        {
                            if (clip == null) continue;
                            foreach (var b in AnimationUtility.GetCurveBindings(clip))
                            {
                                if (b.type != typeof(Transform)) continue;
                                var grp = Group(b.propertyName);
                                if (grp == null) continue;
                                var key = b.path + "|" + grp;
                                if (!map.TryGetValue(key, out var byLayer)) map[key] = byLayer = new Dictionary<string, SortedSet<string>>();
                                var ln = li.Index + ":" + li.Layer.name;
                                if (!byLayer.TryGetValue(ln, out var comps)) byLayer[ln] = comps = new SortedSet<string>();
                                var dot = b.propertyName.LastIndexOf('.');
                                comps.Add(dot >= 0 ? b.propertyName.Substring(dot + 1) : b.propertyName);
                            }
                        }
                var playable = byCtrl.First().Playable;
                foreach (var kv in map)
                {
                    if (kv.Value.Count < 2) continue;
                    var sets = kv.Value.Values.Select(v => string.Join(",", v)).Distinct().ToList();
                    if (sets.Count < 2) continue;
                    var parts = kv.Key.Split('|');
                    var f = new Finding
                    {
                        RuleId = Id, Severity = Severity.Warning, Title = Title,
                        Playable = playable, Layer = "(multiple)", ObjectPath = parts[0], Key = parts[0] + ":" + parts[1],
                        Message = playable + " で「" + parts[0] + "」の " + parts[1] + " を複数レイヤーが異なる軸で書いています（後のレイヤーが全軸を上書き）",
                        Fix = "同じ Transform の同じ種類は1レイヤーにまとめる（2D BlendTree FreeformCartesian2D 等）。別 Transform に分ける手もある",
                    };
                    foreach (var l in kv.Value) f.Evidence.Add("layer " + l.Key + " writes {" + string.Join(",", l.Value) + "}");
                    results.Add(f);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    sealed class R005_BlendTreeClipNameClash : ILintRule
    {
        public string Id => "R005";
        public string Title => "BlendTree 内で別々のクリップが同じ名前";
        public string Description =>
            "1つの BlendTree（入れ子含む）に同名の別クリップがあると、生成スクリプトの名前検索や上書きで取り違える。サブアセット名の重複も起こしやすい。";

        public void Run(LintContext ctx, List<Finding> results)
        {
            var seen = new HashSet<BlendTree>();
            foreach (var li in ctx.Layers)
                foreach (var kv in li.Graph.StatePath)
                {
                    if (!(li.MotionOf(kv.Key) is BlendTree bt) || !seen.Add(bt)) continue;
                    foreach (var grp in SmGraph.Clips(bt).Where(c => c != null).Distinct().GroupBy(c => c.name))
                    {
                        if (grp.Count() < 2) continue;
                        results.Add(new Finding
                        {
                            RuleId = Id, Severity = Severity.Warning, Title = Title,
                            Playable = li.Playable, Layer = li.Layer.name, State = kv.Value, Key = bt.name + ":" + grp.Key,
                            Message = li.Playable + "/" + li.Layer.name + "/" + kv.Value + " の BlendTree「" + bt.name + "」に別クリップ「" + grp.Key + "」が " + grp.Count() + " 個あります",
                            Fix = "クリップ名を一意にする（生成コードでは軸や値を名前に含める）",
                        });
                    }
                }
        }
    }

    // ------------------------------------------------------------------
    sealed class R006_DriverFloatToIntTruncation : ILintRule
    {
        public string Id => "R006";
        public string Title => "Parameter Driver の float→int 変換が切り捨てになる";
        public string Description =>
            "VRChat の Parameter Driver は float を int に入れるとき切り捨てる（公式仕様）。Copy の変換先範囲を +0.5 ずらすと四捨五入になる" +
            "（例: 位置同期で Proximity→int の量子化がずれた件。[0,254]→[0.5,254.5]）。";

        static bool Frac(float v) => Mathf.Abs(v - Mathf.Round(v)) > 1e-4f;

        public void Run(LintContext ctx, List<Finding> results)
        {
            foreach (var dr in ctx.Drivers)
                foreach (var p in dr.Driver.parameters)
                {
                    var dest = ctx.TypeOf(p.name);
                    if (dest != "Int") continue;
                    Finding f = null;
                    if (p.type == VRC_AvatarParameterDriver.ChangeType.Copy && ctx.TypeOf(p.source) == "Float")
                    {
                        if (!p.convertRange)
                            f = Make(dr, p.name, "Copy " + p.source + "(float) → " + p.name + "(int) が範囲変換なしで切り捨てられます",
                                "Convert Range をオンにし、変換先を [min+0.5, max+0.5] にして四捨五入化する", Severity.Warning);
                        else if (!Frac(p.destMin) && !Frac(p.destMax))
                            f = Make(dr, p.name, "Copy " + p.source + " → " + p.name + "(int) の変換先 [" + p.destMin + "," + p.destMax + "] は切り捨てで値が1ずれやすい",
                                "変換先を [" + (p.destMin + 0.5f) + "," + (p.destMax + 0.5f) + "] にすると四捨五入になる", Severity.Warning);
                    }
                    else if (p.type == VRC_AvatarParameterDriver.ChangeType.Set && Frac(p.value))
                        f = Make(dr, p.name, "Set " + p.name + "(int) = " + p.value + " は切り捨てで " + Mathf.Floor(p.value) + " になります",
                            "整数値を設定する", Severity.Info);
                    if (f != null) { f.Key = p.type + ":" + p.name; results.Add(f); }
                }
        }

        Finding Make(DriverRef dr, string param, string msg, string fix, Severity sev) => new Finding
        {
            RuleId = Id, Severity = sev, Title = Title,
            Playable = dr.Playable, Layer = dr.Layer, State = dr.State, Param = param,
            Message = dr.Playable + "/" + dr.Layer + "/" + dr.State + ": " + msg, Fix = fix,
        };
    }
}
#endif