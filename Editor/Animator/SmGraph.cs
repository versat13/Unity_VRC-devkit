using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Versat.VrcDevKit
{
    /// <summary>Flattened view of a layer's state machine with (over-approximating) reachability.</summary>
    public sealed class SmGraph
    {
        public readonly AnimatorStateMachine Root;
        public readonly List<AnimatorStateMachine> Machines = new List<AnimatorStateMachine>();
        public readonly Dictionary<AnimatorStateMachine, AnimatorStateMachine> Parent = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
        public readonly Dictionary<AnimatorStateMachine, string> MachinePath = new Dictionary<AnimatorStateMachine, string>();
        public readonly Dictionary<AnimatorState, string> StatePath = new Dictionary<AnimatorState, string>();
        public readonly Dictionary<AnimatorState, AnimatorStateMachine> Owner = new Dictionary<AnimatorState, AnimatorStateMachine>();

        public SmGraph(AnimatorStateMachine root)
        {
            Root = root;
            if (root != null) Walk(root, null, "");
        }

        void Walk(AnimatorStateMachine sm, AnimatorStateMachine parent, string prefix)
        {
            if (sm == null || MachinePath.ContainsKey(sm)) return;
            Machines.Add(sm);
            MachinePath[sm] = prefix;
            if (parent != null) Parent[sm] = parent;
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                StatePath[cs.state] = prefix + cs.state.name;
                Owner[cs.state] = sm;
            }
            foreach (var csm in sm.stateMachines)
                if (csm.stateMachine != null) Walk(csm.stateMachine, sm, prefix + csm.stateMachine.name + "/");
        }

        public string PathOf(AnimatorState s) => s != null && StatePath.TryGetValue(s, out var p) ? p : (s != null ? s.name : "?");

        public void EntryTargets(AnimatorStateMachine sm, Func<AnimatorTransitionBase, bool> allowed, HashSet<AnimatorStateMachine> guard, List<AnimatorState> outList)
        {
            if (sm == null || !guard.Add(sm)) return;
            if (sm.defaultState != null) outList.Add(sm.defaultState);
            foreach (var t in sm.entryTransitions)
                if (t != null && allowed(t)) Targets(t, sm, allowed, guard, outList);
        }

        public void Targets(AnimatorTransitionBase t, AnimatorStateMachine owner, Func<AnimatorTransitionBase, bool> allowed, HashSet<AnimatorStateMachine> guard, List<AnimatorState> outList)
        {
            if (t.destinationState != null) { outList.Add(t.destinationState); return; }
            if (t.destinationStateMachine != null) { EntryTargets(t.destinationStateMachine, allowed, guard, outList); return; }
            if (t.isExit)
            {
                if (owner != null && Parent.TryGetValue(owner, out var p))
                    foreach (var st in p.GetStateMachineTransitions(owner))
                        if (st != null && allowed(st)) Targets(st, p, allowed, guard, outList);
                EntryTargets(Root, allowed, guard, outList); // over-approximation
            }
        }

        /// <summary>States reachable when every transition passes <paramref name="allowed"/>.</summary>
        public HashSet<AnimatorState> Reachable(Func<AnimatorTransitionBase, bool> allowed)
        {
            var seen = new HashSet<AnimatorState>();
            var q = new Queue<AnimatorState>();
            var buf = new List<AnimatorState>();
            void Flush()
            {
                foreach (var s in buf) if (s != null && seen.Add(s)) q.Enqueue(s);
                buf.Clear();
            }
            if (Root == null) return seen;
            EntryTargets(Root, allowed, new HashSet<AnimatorStateMachine>(), buf); Flush();
            foreach (var sm in Machines)
                foreach (var t in sm.anyStateTransitions)
                    if (t != null && allowed(t)) { Targets(t, sm, allowed, new HashSet<AnimatorStateMachine>(), buf); Flush(); }
            while (q.Count > 0)
            {
                var s = q.Dequeue();
                Owner.TryGetValue(s, out var owner);
                foreach (var t in s.transitions)
                    if (t != null && allowed(t)) { Targets(t, owner, allowed, new HashSet<AnimatorStateMachine>(), buf); Flush(); }
            }
            return seen;
        }

        /// <summary>True when the transition can only fire for the local player (has an IsLocal==true condition).</summary>
        public static bool RequiresLocal(AnimatorTransitionBase t)
        {
            foreach (var c in t.conditions)
            {
                if (c.parameter != "IsLocal") continue;
                switch (c.mode)
                {
                    case AnimatorConditionMode.If: return true;
                    case AnimatorConditionMode.Equals: if (Mathf.Abs(c.threshold) > 0.001f) return true; break;
                    case AnimatorConditionMode.Greater: if (c.threshold >= 0f && c.threshold < 1f) return true; break;
                    case AnimatorConditionMode.NotEqual: if (Mathf.Abs(c.threshold) < 0.001f) return true; break;
                }
            }
            return false;
        }

        public static string ConditionText(AnimatorCondition c)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If: return c.parameter;
                case AnimatorConditionMode.IfNot: return "!" + c.parameter;
                case AnimatorConditionMode.Greater: return c.parameter + ">" + c.threshold;
                case AnimatorConditionMode.Less: return c.parameter + "<" + c.threshold;
                case AnimatorConditionMode.Equals: return c.parameter + "==" + c.threshold;
                case AnimatorConditionMode.NotEqual: return c.parameter + "!=" + c.threshold;
                default: return c.parameter + "?" + c.threshold;
            }
        }

        public static IEnumerable<AnimationClip> Clips(Motion m)
        {
            if (m is AnimationClip c) { yield return c; yield break; }
            if (m is BlendTree bt)
                foreach (var ch in bt.children)
                    foreach (var cc in Clips(ch.motion)) yield return cc;
        }

        /// <summary>Animator parameters read by a motion (blend parameters, recursively).</summary>
        public static IEnumerable<string> MotionParams(Motion m)
        {
            if (!(m is BlendTree bt)) yield break;
            switch (bt.blendType)
            {
                case BlendTreeType.Simple1D:
                    yield return bt.blendParameter; break;
                case BlendTreeType.Direct:
                    foreach (var ch in bt.children) yield return ch.directBlendParameter; break;
                default:
                    yield return bt.blendParameter; yield return bt.blendParameterY; break;
            }
            foreach (var ch in bt.children)
                foreach (var p in MotionParams(ch.motion)) yield return p;
        }

        public static string MotionLabel(Motion m)
        {
            if (m == null) return null;
            if (m is BlendTree bt) return "BT(" + bt.blendType + "):" + bt.name;
            return m.name;
        }
    }
}