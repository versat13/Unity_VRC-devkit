#if VRCDK_AVATARS
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Versat.VrcDevKit
{
    /// <summary>Regression cases reproducing past real bugs. Each builds an in-memory controller (no assets, no scene).</summary>
    public static class SelfTest
    {
        sealed class Case
        {
            public string Name, Rule, Param;
            public bool Expect;
            public System.Action<LintContext> Build;
        }

        static AnimatorController Ctrl(LintContext ctx, string playable = "FX")
        {
            var ac = new AnimatorController { name = "SelfTest" };
            ctx.Controllers.Add(new ControllerRef { Playable = playable, Controller = ac });
            return ac;
        }

        static AnimatorStateMachine Layer(AnimatorController ac, string name)
        {
            ac.AddLayer(name);
            return ac.layers[ac.layers.Length - 1].stateMachine;
        }

        static AnimatorStateTransition Tr(AnimatorState a, AnimatorState b, bool exitTime = false)
        {
            var t = a.AddTransition(b);
            t.hasExitTime = exitTime; t.exitTime = 0; t.duration = 0;
            return t;
        }

        static AnimationClip Clip(string name, string path, string prop)
        {
            var c = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(c, EditorCurveBinding.FloatCurve(path, typeof(Transform), prop), AnimationCurve.Constant(0, 1, 0));
            return c;
        }

        static void ParamZoomLayer(LintContext ctx, bool synced, bool gateLocal)
        {
            var ac = Ctrl(ctx);
            ac.AddParameter("Zoom", AnimatorControllerParameterType.Int);
            ac.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var sm = Layer(ac, "Look");
            var idle = sm.AddState("Idle"); var show = sm.AddState("Show");
            var t = Tr(idle, show);
            t.AddCondition(AnimatorConditionMode.Greater, 0, "Zoom");
            if (gateLocal) t.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            ctx.Expr["Zoom"] = new ExprParamInfo { Name = "Zoom", Type = "Int", Synced = synced };
        }

        static readonly List<Case> Cases = new List<Case>
        {
            new Case { Name = "R003 stuck transition (shutter Review->WaitRelease)", Rule = "R003", Expect = true, Build = ctx => {
                var sm = Layer(Ctrl(ctx), "Shoot"); var a = sm.AddState("Review"); var b = sm.AddState("WaitRelease"); Tr(a, b); } },
            new Case { Name = "R003 ok with exit time", Rule = "R003", Expect = false, Build = ctx => {
                var sm = Layer(Ctrl(ctx), "Shoot"); var a = sm.AddState("Review"); var b = sm.AddState("WaitRelease"); Tr(a, b, true); } },
            new Case { Name = "R001 unsynced setting read remotely (camera settings)", Rule = "R001", Param = "Zoom", Expect = true, Build = ctx => ParamZoomLayer(ctx, false, false) },
            new Case { Name = "R001 ok when synced", Rule = "R001", Param = "Zoom", Expect = false, Build = ctx => ParamZoomLayer(ctx, true, false) },
            new Case { Name = "R001 ok when gated by IsLocal", Rule = "R001", Param = "Zoom", Expect = false, Build = ctx => ParamZoomLayer(ctx, false, true) },
            new Case { Name = "R001 ok when copied by remote driver", Rule = "R001", Param = "Zoom", Expect = false, Build = ctx => {
                ParamZoomLayer(ctx, false, false);
                var ac = ctx.Controllers[0].Controller; ac.AddParameter("ZoomSync", AnimatorControllerParameterType.Int);
                var sm = Layer(ac, "Copy"); var s = sm.AddState("Run");
                var d = s.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                d.parameters = new List<VRC_AvatarParameterDriver.Parameter> { new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, name = "Zoom", source = "ZoomSync" } };
                ctx.Expr["ZoomSync"] = new ExprParamInfo { Name = "ZoomSync", Type = "Int", Synced = true }; } },
            new Case { Name = "R002 PhysBone IsGrabbed read remotely (pen/board grab)", Rule = "R002", Param = "Pen_IsGrabbed", Expect = true, Build = ctx => {
                var ac = Ctrl(ctx); ac.AddParameter("Pen_IsGrabbed", AnimatorControllerParameterType.Bool);
                var sm = Layer(ac, "PenDraw"); var a = sm.AddState("Off"); var b = sm.AddState("On");
                Tr(a, b).AddCondition(AnimatorConditionMode.If, 0, "Pen_IsGrabbed");
                ctx.AddPhysBonePrefix("Pen", "PenRack"); } },
            new Case { Name = "R002 localOnly contact read remotely", Rule = "R002", Param = "PS_Major", Expect = true, Build = ctx => {
                var ac = Ctrl(ctx); ac.AddParameter("PS_Major", AnimatorControllerParameterType.Float);
                var sm = Layer(ac, "Measure"); var a = sm.AddState("A"); var b = sm.AddState("B");
                Tr(a, b).AddCondition(AnimatorConditionMode.Greater, 0.5f, "PS_Major");
                ctx.ContactParams["PS_Major"] = new ContactParamInfo { Path = "PosSync/Recv", LocalOnly = true }; } },
            new Case { Name = "R004 split localPosition axes across layers", Rule = "R004", Expect = true, Build = ctx => {
                var ac = Ctrl(ctx);
                Layer(ac, "MoveX").AddState("X").motion = Clip("MX", "NMove", "m_LocalPosition.x");
                Layer(ac, "MoveY").AddState("Y").motion = Clip("MY", "NMove", "m_LocalPosition.y"); } },
            new Case { Name = "R004 ok when same axes", Rule = "R004", Expect = false, Build = ctx => {
                var ac = Ctrl(ctx);
                Layer(ac, "A").AddState("X").motion = Clip("MX", "NMove", "m_LocalPosition.x");
                Layer(ac, "B").AddState("X").motion = Clip("MX2", "NMove", "m_LocalPosition.x"); } },
            new Case { Name = "R005 blend tree clip name clash", Rule = "R005", Expect = true, Build = ctx => {
                var ac = Ctrl(ctx); ac.AddParameter("F", AnimatorControllerParameterType.Float);
                var bt = new BlendTree { name = "Adj", blendType = BlendTreeType.Simple1D, blendParameter = "F" };
                bt.AddChild(Clip("Same", "A", "m_LocalPosition.x"), 0); bt.AddChild(Clip("Same", "A", "m_LocalPosition.x"), 1);
                Layer(ac, "Adj").AddState("BT").motion = bt; } },
            new Case { Name = "R006 driver copy float->int truncation (PosSync quantize)", Rule = "R006", Expect = true, Build = ctx => DriverCase(ctx, true, 0f, 254f) },
            new Case { Name = "R006 ok with +0.5 dest range", Rule = "R006", Expect = false, Build = ctx => DriverCase(ctx, true, 0.5f, 254.5f) },
        };

        static void DriverCase(LintContext ctx, bool convert, float dmin, float dmax)
        {
            var ac = Ctrl(ctx);
            ac.AddParameter("Prox", AnimatorControllerParameterType.Float);
            ac.AddParameter("Digit", AnimatorControllerParameterType.Int);
            var s = Layer(ac, "Quantize").AddState("Copy");
            var d = s.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            d.parameters = new List<VRC_AvatarParameterDriver.Parameter> { new VRC_AvatarParameterDriver.Parameter {
                type = VRC_AvatarParameterDriver.ChangeType.Copy, name = "Digit", source = "Prox", convertRange = convert,
                sourceMin = 0, sourceMax = 1, destMin = dmin, destMax = dmax } };
        }

        public static string Run()
        {
            var w = new JsonWriter().BeginObject();
            int pass = 0, fail = 0;
            w.Name("cases").BeginArray();
            foreach (var c in Cases)
            {
                var ctx = new LintContext { AvatarName = "selftest" };
                string detail = null; bool ok;
                try
                {
                    c.Build(ctx);
                    var errs = new List<string>();
                    var fs = Linter.Run(ctx, new[] { c.Rule }, errs);
                    bool hit = fs.Any(f => f.RuleId == c.Rule && (c.Param == null || f.Param == c.Param));
                    ok = hit == c.Expect && errs.Count == 0;
                    detail = (errs.Count > 0 ? "errors: " + string.Join("; ", errs) + " " : "") + "found=" + string.Join(" | ", fs.Select(f => f.Id));
                }
                catch (System.Exception e) { ok = false; detail = e.GetType().Name + ": " + e.Message; }
                finally { foreach (var cr in ctx.Controllers) Object.DestroyImmediate(cr.Controller); }
                if (ok) pass++; else fail++;
                w.BeginObject().Prop("name", c.Name).Prop("rule", c.Rule).Prop("expect", c.Expect).Prop("pass", ok).Prop("detail", detail).EndObject();
            }
            w.EndArray();
            w.Prop("ok", fail == 0).Prop("pass", pass).Prop("fail", fail).EndObject();
            return w.ToString();
        }
    }
}
#endif