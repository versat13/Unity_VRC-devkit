#if VRCDK_AVATARS
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Versat.VrcDevKit
{
    public sealed class ExprParamInfo
    {
        public string Name;
        public string Type; // Bool / Int / Float
        public bool Synced;
        public bool Saved;
        public float Default;
        public int Bits => !Synced ? 0 : Type == "Bool" ? 1 : 8;
    }

    public sealed class ControllerRef
    {
        public string Playable;
        public AnimatorController Controller;
    }

    public sealed class ParamUse
    {
        public string Param, Playable, Layer, State, Usage;
        public bool Remote;
    }

    public sealed class ParamWriter
    {
        public string Param, Kind, Where;
        public bool Remote;
    }

    public sealed class DriverRef
    {
        public string Playable, Layer, State;
        public VRCAvatarParameterDriver Driver;
        public bool Remote;
    }

    public sealed class LayerInfo
    {
        public string Playable;
        public AnimatorController Controller;
        public int Index;
        public AnimatorControllerLayer Layer;
        public bool IsSynced;
        public SmGraph Graph;
        public HashSet<AnimatorState> RemoteReachable;
        public Motion MotionOf(AnimatorState s) => IsSynced ? Layer.GetOverrideMotion(s) : s.motion;
        public StateMachineBehaviour[] BehavioursOf(AnimatorState s) => IsSynced ? Layer.GetOverrideBehaviours(s) : s.behaviours;
    }

    public sealed class ContactParamInfo
    {
        public string Path;
        public bool LocalOnly;
    }

    /// <summary>
    /// Everything a rule needs, independent of the scene (rules can be unit-tested with synthetic contexts).
    /// Fill the inputs, then call <see cref="Analyze"/>.
    /// </summary>
    public sealed class LintContext
    {
        // ---- inputs
        public string AvatarName;
        public bool Processed;
        public Transform Root;
        public readonly List<ControllerRef> Controllers = new List<ControllerRef>();
        public readonly Dictionary<string, ExprParamInfo> Expr = new Dictionary<string, ExprParamInfo>();
        public readonly Dictionary<string, ContactParamInfo> ContactParams = new Dictionary<string, ContactParamInfo>();
        public readonly Dictionary<string, string> PhysBoneParams = new Dictionary<string, string>();
        public readonly List<string> Notes = new List<string>();
        /// <summary>True when the expressions menu was read; then only menu-referenced non-synced params count as local writers.</summary>
        public bool MenuKnown;
        public readonly HashSet<string> MenuParams = new HashSet<string>();

        // ---- derived
        public readonly List<LayerInfo> Layers = new List<LayerInfo>();
        public readonly List<ParamUse> Uses = new List<ParamUse>();
        public readonly List<ParamWriter> Writers = new List<ParamWriter>();
        public readonly List<DriverRef> Drivers = new List<DriverRef>();
        public readonly Dictionary<string, AnimatorControllerParameterType> ParamTypes = new Dictionary<string, AnimatorControllerParameterType>();
        public readonly HashSet<string> RemoteAvailable = new HashSet<string>();
        bool _analyzed;

        public static readonly HashSet<string> Builtins = new HashSet<string>
        {
            "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK",
            "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled",
            "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent"
        };

        public static readonly string[] PhysBoneSuffixes = { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };

        public string RelPath(Transform t) => Root == null || t == null ? (t != null ? t.name : "") : AnimationUtility.CalculateTransformPath(t, Root);

        public void AddPhysBonePrefix(string prefix, string path)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            foreach (var s in PhysBoneSuffixes) PhysBoneParams[prefix + s] = path;
        }

        public string TypeOf(string param)
        {
            if (param == null) return null;
            if (ParamTypes.TryGetValue(param, out var t)) return t.ToString();
            if (Expr.TryGetValue(param, out var e)) return e.Type;
            return null;
        }

        public void Analyze()
        {
            if (_analyzed) return;
            _analyzed = true;

            foreach (var e in Expr.Values)
            {
                if (e.Synced) Writers.Add(new ParamWriter { Param = e.Name, Kind = "expression(synced)", Where = "ExpressionParameters", Remote = true });
                else if (!MenuKnown || MenuParams.Contains(e.Name)) Writers.Add(new ParamWriter { Param = e.Name, Kind = "menu(local)", Where = "ExpressionsMenu", Remote = false });
                // non-synced and not in any menu: constant default unless something else writes it
            }
            foreach (var kv in ContactParams)
                Writers.Add(new ParamWriter { Param = kv.Key, Kind = kv.Value.LocalOnly ? "contact(localOnly)" : "contact", Where = kv.Value.Path, Remote = !kv.Value.LocalOnly });
            foreach (var kv in PhysBoneParams)
                Writers.Add(new ParamWriter { Param = kv.Key, Kind = "physbone", Where = kv.Value, Remote = true });

            foreach (var cref in Controllers)
            {
                var ac = cref.Controller;
                if (ac == null) continue;
                foreach (var p in ac.parameters)
                    if (!ParamTypes.ContainsKey(p.name)) ParamTypes[p.name] = p.type;

                var layers = ac.layers;
                for (int i = 0; i < layers.Length; i++)
                {
                    var L = layers[i];
                    bool synced = L.syncedLayerIndex >= 0 && L.syncedLayerIndex < layers.Length;
                    var sm = synced ? layers[L.syncedLayerIndex].stateMachine : L.stateMachine;
                    var g = new SmGraph(sm);
                    var reach = g.Reachable(t => !SmGraph.RequiresLocal(t));
                    var li = new LayerInfo { Playable = cref.Playable, Controller = ac, Index = i, Layer = L, IsSynced = synced, Graph = g, RemoteReachable = reach };
                    Layers.Add(li);
                    AnalyzeLayer(li);
                }
            }

            foreach (var b in Builtins) RemoteAvailable.Add(b);
            foreach (var w in Writers) if (w.Remote) RemoteAvailable.Add(w.Param);
        }

        void Use(LayerInfo li, string param, string state, string usage, bool remote)
        {
            if (string.IsNullOrEmpty(param)) return;
            Uses.Add(new ParamUse { Param = param, Playable = li.Playable, Layer = li.Layer.name, State = state, Usage = usage, Remote = remote });
        }

        void AnalyzeLayer(LayerInfo li)
        {
            var g = li.Graph;
            foreach (var kv in g.StatePath)
            {
                var s = kv.Key;
                var path = kv.Value;
                bool r = li.RemoteReachable.Contains(s);
                var m = li.MotionOf(s);
                foreach (var p in SmGraph.MotionParams(m)) Use(li, p, path, "blend", r);
                if (s.speedParameterActive) Use(li, s.speedParameter, path, "speed", r);
                if (s.timeParameterActive) Use(li, s.timeParameter, path, "motionTime", r);
                if (s.mirrorParameterActive) Use(li, s.mirrorParameter, path, "mirror", r);
                if (s.cycleOffsetParameterActive) Use(li, s.cycleOffsetParameter, path, "cycleOffset", r);

                if (!li.IsSynced)
                    foreach (var t in s.transitions)
                    {
                        if (t == null) continue;
                        bool tr = r && !SmGraph.RequiresLocal(t);
                        var dest = t.destinationState != null ? g.PathOf(t.destinationState) : t.destinationStateMachine != null ? t.destinationStateMachine.name + "/" : "Exit";
                        foreach (var c in t.conditions) Use(li, c.parameter, path, "condition(->" + dest + ")", tr);
                    }

                var where = li.Playable + "/" + li.Layer.name + "/" + path;
                var bs = li.BehavioursOf(s);
                if (bs != null)
                    foreach (var b in bs)
                    {
                        if (!(b is VRCAvatarParameterDriver d)) continue;
                        bool dr = r && !d.localOnly;
                        Drivers.Add(new DriverRef { Playable = li.Playable, Layer = li.Layer.name, State = path, Driver = d, Remote = dr });
                        foreach (var prm in d.parameters)
                        {
                            Writers.Add(new ParamWriter { Param = prm.name, Kind = d.localOnly ? "driver(localOnly)" : (r ? "driver" : "driver(local-only state)"), Where = where, Remote = dr });
                            if (prm.type == VRC_AvatarParameterDriver.ChangeType.Copy) Use(li, prm.source, path, "driver-copy-source", dr);
                        }
                    }

                foreach (var clip in SmGraph.Clips(m))
                {
                    if (clip == null) continue;
                    foreach (var bnd in AnimationUtility.GetCurveBindings(clip))
                        if (bnd.type == typeof(Animator) && string.IsNullOrEmpty(bnd.path))
                            Writers.Add(new ParamWriter { Param = bnd.propertyName, Kind = "animated(AAP)", Where = where, Remote = r });
                }
            }

            if (li.IsSynced) return;
            foreach (var sm in g.Machines)
            {
                var prefix = g.MachinePath[sm];
                foreach (var t in sm.anyStateTransitions)
                {
                    if (t == null) continue;
                    bool tr = !SmGraph.RequiresLocal(t);
                    var dest = t.destinationState != null ? g.PathOf(t.destinationState) : "?";
                    foreach (var c in t.conditions) Use(li, c.parameter, prefix + "AnyState", "condition(->" + dest + ")", tr);
                }
                foreach (var t in sm.entryTransitions)
                {
                    if (t == null) continue;
                    bool tr = !SmGraph.RequiresLocal(t);
                    foreach (var c in t.conditions) Use(li, c.parameter, prefix + "Entry", "entry-condition", tr);
                }
            }
        }
    }
}
#endif