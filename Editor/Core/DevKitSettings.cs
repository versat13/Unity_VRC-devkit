using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Versat.VrcDevKit
{
    /// <summary>Project-level settings stored in ProjectSettings/VrcDevKitSettings.json (version-controlled).</summary>
    [Serializable]
    public sealed class DevKitSettings
    {
        public const string FilePath = "ProjectSettings/VrcDevKitSettings.json";

        [Serializable]
        public sealed class Suppression
        {
            public string id;
            public string reason;
        }

        /// <summary>Avatar GameObject names that DevKit must never clone, process or analyse.</summary>
        public List<string> excludedAvatars = new List<string>();
        /// <summary>Finding ids that are intentional. Matching findings are marked suppressed.</summary>
        public List<Suppression> suppressions = new List<Suppression>();
        [Serializable]
        public sealed class OwnerOnlyLayer
        {
            /// <summary>Regex matched against "Playable/LayerName" (e.g. "FX/SorenariCamera_Debug$").</summary>
            public string pattern;
            public string reason;
        }

        /// <summary>Layers whose effects are only visible to the owner by design (UI, render settings of an owner-only camera...).
        /// R001/R002 findings in these layers are marked suppressed with the reason.</summary>
        public List<OwnerOnlyLayer> ownerOnlyLayers = new List<OwnerOnlyLayer>();
        /// <summary>Report output folder, relative to the project root (outside Assets so nothing is imported).</summary>
        public string reportRoot = "DevKitReports";

        static DevKitSettings _cache;

        public static DevKitSettings Load()
        {
            if (_cache != null) return _cache;
            _cache = new DevKitSettings();
            if (File.Exists(FilePath))
            {
                try { JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), _cache); }
                catch (Exception e) { Debug.LogWarning("[VrcDevKit] settings parse failed: " + e.Message); }
            }
            return _cache;
        }

        public void Save()
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            _cache = this;
        }

        public bool IsExcluded(string avatarName) => excludedAvatars.Contains(avatarName);

        public string OwnerOnlyReason(string playable, string layer)
        {
            if (playable == null || layer == null) return null;
            var key = playable + "/" + layer;
            foreach (var o in ownerOnlyLayers)
            {
                if (string.IsNullOrEmpty(o.pattern)) continue;
                try { if (System.Text.RegularExpressions.Regex.IsMatch(key, o.pattern)) return "owner-only layer (" + o.pattern + "): " + o.reason; }
                catch (ArgumentException) { }
            }
            return null;
        }

        public string SuppressReason(string id)
        {
            foreach (var s in suppressions) if (s.id == id) return string.IsNullOrEmpty(s.reason) ? "(no reason)" : s.reason;
            return null;
        }
    }
}