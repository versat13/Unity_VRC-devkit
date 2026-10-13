using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Versat.VrcDevKit
{
    /// <summary>Thin human UI. All logic lives in <see cref="Api"/> so LLMs and humans see the same results.</summary>
    public sealed class DevKitWindow : EditorWindow
    {
        string _avatar = "";
        bool _ndmf = true;
        string _status = "";
        Vector2 _scroll;

        [MenuItem("Tools/VRC DevKit/Window")]
        static void Open() => GetWindow<DevKitWindow>("VRC DevKit");

        [MenuItem("Tools/VRC DevKit/Lint Selected Avatar")]
        static void LintSelected() => Debug.Log("[VrcDevKit] " + Api.Lint(null));

        [MenuItem("Tools/VRC DevKit/Run Self Test")]
        static void SelfTestMenu() => Debug.Log("[VrcDevKit] " + Api.RunSelfTest());

        [MenuItem("Tools/VRC DevKit/Open Reports Folder")]
        static void OpenReports()
        {
            var dir = System.IO.Path.GetFullPath(DevKitSettings.Load().reportRoot);
            System.IO.Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("VRC DevKit " + Api.Version, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("LLM からは Versat.VrcDevKit.Api（execute_code）か MCP ツール vrc_devkit で同じ操作ができます。解析は一時クローンで行い、元のアバターは変更しません。", MessageType.None);
            _avatar = EditorGUILayout.TextField("Avatar (空=選択中)", _avatar);
            _ndmf = EditorGUILayout.Toggle("NDMF/MA を適用して解析", _ndmf);
            using (new EditorGUILayout.HorizontalScope())
            {
                var opt = "{\"ndmf\":" + (_ndmf ? "true" : "false") + "}";
                if (GUILayout.Button("Lint")) _status = Api.Lint(_avatar, opt);
                if (GUILayout.Button("Dump")) _status = Api.Dump(_avatar, opt);
                if (GUILayout.Button("Self Test")) _status = Api.RunSelfTest();
                if (GUILayout.Button("Reports")) OpenReports();
            }
            EditorGUILayout.LabelField("Last result", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var f in Api.LastFindings.Take(200))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var style = new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true };
                    var color = f.Severity == Severity.Error ? "#ff6060" : f.Severity == Severity.Warning ? "#ffb040" : "#a0a0a0";
                    GUILayout.Label("<color=" + color + ">[" + Finding.SevChar(f.Severity) + "] " + f.RuleId + "</color> " + f.Message + (f.SuppressedReason != null ? " (suppressed)" : ""), style);
                    GUILayout.Label("fix: " + f.Fix, EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (!string.IsNullOrEmpty(f.ObjectPath) && GUILayout.Button("Select", GUILayout.Width(60))) Api.Select(Api.LastAvatar, f.ObjectPath);
                        if (GUILayout.Button("Copy id", GUILayout.Width(70))) EditorGUIUtility.systemCopyBuffer = f.Id;
                        if (f.SuppressedReason == null && GUILayout.Button("Suppress", GUILayout.Width(70))) { Api.Suppress(f.Id, "suppressed from window"); f.SuppressedReason = "suppressed from window"; }
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.TextArea(_status, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndScrollView();
        }
    }
}