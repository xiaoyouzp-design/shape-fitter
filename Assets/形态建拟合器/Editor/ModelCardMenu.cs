using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>Menu shortcuts for the most common workflows.</summary>
    public static class ModelCardMenu
    {
        public static void OpenWindow()
        {
            ModelCardWindow.Open();
        }

        [MenuItem("Tools/VRC Model Card/Capture Card (PNG)")]
        public static void CaptureCard()
        {
            var w = WindowForSelection();
            if (w != null) w.CaptureToPng();
        }

        [MenuItem("Tools/VRC Model Card/Apply Card to Selection (PNG/JSON)")]
        public static void ApplyCard()
        {
            var w = WindowForSelection();
            if (w != null) w.LoadAndApply();
        }

        [MenuItem("Tools/VRC Model Card/Generate FX Layer from Card (PNG)")]
        public static void GenerateFxFromCard()
        {
            var w = WindowForSelection();
            if (w != null) w.GenerateFxFromPng();
        }

        /// <summary>Remove the embedded card chunk from a PNG (privacy / clean art).</summary>
        [MenuItem("Tools/VRC Model Card/Strip Card Data from PNG")]
        public static void StripCard()
        {
            string path = EditorUtility.OpenFilePanel("Select PNG to strip", "", "png");
            if (string.IsNullOrEmpty(path)) return;
            byte[] png = File.ReadAllBytes(path);
            if (!PngCodec.IsPng(png))
            {
                EditorUtility.DisplayDialog("VRC Model Card", "Not a PNG file.", "OK");
                return;
            }
            byte[] stripped = PngCodec.StripChunk(png);
            File.WriteAllBytes(path, stripped);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("VRC Model Card",
                "Card chunk removed. Note: the LSB pixel backup (if any) is not removed by this action.",
                "OK");
        }

        private static ModelCardWindow WindowForSelection()
        {
            var root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("VRC Model Card", "Select an avatar root GameObject first.", "OK");
                return null;
            }
            var w = EditorWindow.GetWindow<ModelCardWindow>("VRC Model Card");
            w.root = root;
            return w;
        }
    }
}
