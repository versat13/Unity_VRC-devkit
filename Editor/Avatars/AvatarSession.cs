#if VRCDK_AVATARS
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Versat.VrcDevKit
{
    /// <summary>
    /// Opens an avatar for analysis. In edit mode it analyses a temporary clone after NDMF/MA processing
    /// (so MA-merged layers are included) and destroys the clone on Dispose. The source avatar is never modified.
    /// In play mode the live (already processed) avatar is read as-is.
    /// </summary>
    public sealed class AvatarSession : IDisposable
    {
        public string SourceName;
        public string SourcePath;
        public GameObject Root;
        public VRCAvatarDescriptor Descriptor;
        public bool Processed;
        public readonly List<string> Notes = new List<string>();
        GameObject _clone;

        public static List<VRCAvatarDescriptor> FindAll()
        {
            var list = new List<VRCAvatarDescriptor>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (var go in sc.GetRootGameObjects())
                    list.AddRange(go.GetComponentsInChildren<VRCAvatarDescriptor>(true));
            }
            return list.Where(d => d != null && !d.gameObject.name.EndsWith("(VrcDevKit)")).ToList();
        }

        public static string HierarchyPath(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        /// <summary>Find by exact name, then exact hierarchy path, then unique case-insensitive substring. Excluded avatars are never returned.</summary>
        public static VRCAvatarDescriptor Find(string query, out string error)
        {
            error = null;
            var settings = DevKitSettings.Load();
            var all = FindAll();
            if (string.IsNullOrEmpty(query))
            {
                var sel = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
                if (sel != null) query = sel.gameObject.name;
                else { error = "avatar not specified and no avatar selected. available: " + string.Join(", ", all.Select(a => a.gameObject.name)); return null; }
            }
            var hits = all.Where(a => a.gameObject.name == query).ToList();
            if (hits.Count == 0) hits = all.Where(a => HierarchyPath(a.transform) == query).ToList();
            if (hits.Count == 0) hits = all.Where(a => a.gameObject.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hits.Count == 0) { error = "avatar '" + query + "' not found. available: " + string.Join(", ", all.Select(a => a.gameObject.name)); return null; }
            if (hits.Count > 1) { error = "avatar '" + query + "' is ambiguous: " + string.Join(", ", hits.Select(a => HierarchyPath(a.transform))); return null; }
            if (settings.IsExcluded(hits[0].gameObject.name)) { error = "avatar '" + hits[0].gameObject.name + "' is excluded in " + DevKitSettings.FilePath + " (DevKit never touches it)"; return null; }
            return hits[0];
        }

        public static AvatarSession Open(VRCAvatarDescriptor src, bool useNdmf)
        {
            var s = new AvatarSession { SourceName = src.gameObject.name, SourcePath = HierarchyPath(src.transform) };
            if (Application.isPlaying)
            {
                s.Root = src.gameObject; s.Descriptor = src; s.Processed = true;
                s.Notes.Add("play mode: reading the live avatar (already processed by NDMF apply-on-play)");
                return s;
            }
            if (!useNdmf)
            {
                s.Root = src.gameObject; s.Descriptor = src;
                s.Notes.Add("raw: NDMF/MA not applied (MA-merged layers are missing)");
                return s;
            }
#if VRCDK_NDMF
            var clone = UnityEngine.Object.Instantiate(src.gameObject);
            clone.name = src.gameObject.name + " (VrcDevKit)";
            s._clone = clone;
            s.Root = clone;
            s.Descriptor = clone.GetComponent<VRCAvatarDescriptor>();
            if (!clone.activeSelf) { clone.SetActive(true); s.Notes.Add("source avatar is inactive; the temporary clone was activated for processing"); }
            try
            {
                nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(clone);
                s.Processed = true;
            }
            catch (Exception e)
            {
                s.Notes.Add("NDMF processing failed, analysing unprocessed clone: " + e.GetType().Name + ": " + e.Message);
            }
#else
            s.Root = src.gameObject; s.Descriptor = src;
            s.Notes.Add("NDMF not installed: analysing raw avatar");
#endif
            return s;
        }

        public void Dispose()
        {
            if (_clone != null)
            {
                UnityEngine.Object.DestroyImmediate(_clone);
                _clone = null;
#if VRCDK_NDMF
                try { nadena.dev.ndmf.AvatarProcessor.CleanTemporaryAssets(); } catch (Exception) { }
#endif
            }
        }

        public LintContext BuildContext()
        {
            var ctx = new LintContext { AvatarName = SourceName, Processed = Processed, Root = Root.transform };
            ctx.Notes.AddRange(Notes);
            var d = Descriptor;
            if (d.customizeAnimationLayers)
            {
                foreach (var l in d.baseAnimationLayers.Concat(d.specialAnimationLayers))
                {
                    if (l.isDefault || l.animatorController == null) continue;
                    if (l.animatorController is AnimatorController ac) ctx.Controllers.Add(new ControllerRef { Playable = l.type.ToString(), Controller = ac });
                    else ctx.Notes.Add(l.type + ": controller is not an AnimatorController (" + l.animatorController.GetType().Name + "), skipped");
                }
            }
            else ctx.Notes.Add("customizeAnimationLayers is off: no custom playable layers");

            if (d.expressionParameters != null && d.expressionParameters.parameters != null)
                foreach (var p in d.expressionParameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    ctx.Expr[p.name] = new ExprParamInfo
                    {
                        Name = p.name, Synced = p.networkSynced, Saved = p.saved, Default = p.defaultValue,
                        Type = p.valueType == VRCExpressionParameters.ValueType.Bool ? "Bool" : p.valueType == VRCExpressionParameters.ValueType.Int ? "Int" : "Float",
                    };
                }

            if (d.expressionsMenu != null)
            {
                ctx.MenuKnown = true;
                CollectMenu(d.expressionsMenu, ctx.MenuParams, new HashSet<VRCExpressionsMenu>());
            }
            else ctx.Notes.Add("no expressions menu: non-synced expression parameters are treated as menu-controlled");

            foreach (var c in Root.GetComponentsInChildren<ContactReceiver>(true))
            {
                if (string.IsNullOrEmpty(c.parameter)) continue;
                var path = ctx.RelPath(c.transform);
                if (ctx.ContactParams.TryGetValue(c.parameter, out var ex)) { ex.LocalOnly &= c.localOnly; continue; }
                ctx.ContactParams[c.parameter] = new ContactParamInfo { Path = path, LocalOnly = c.localOnly };
            }
            foreach (var pb in Root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                ctx.AddPhysBonePrefix(pb.parameter, ctx.RelPath(pb.transform));
            return ctx;
        }

        static void CollectMenu(VRCExpressionsMenu m, HashSet<string> outSet, HashSet<VRCExpressionsMenu> seen)
        {
            if (m == null || !seen.Add(m) || m.controls == null) return;
            foreach (var c in m.controls)
            {
                if (c == null) continue;
                if (c.parameter != null && !string.IsNullOrEmpty(c.parameter.name)) outSet.Add(c.parameter.name);
                if (c.subParameters != null) foreach (var sp in c.subParameters) if (sp != null && !string.IsNullOrEmpty(sp.name)) outSet.Add(sp.name);
                if (c.subMenu != null) CollectMenu(c.subMenu, outSet, seen);
            }
        }
    }
}
#endif