using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public enum CoverSource { Auto, File, Screenshot, Qr }

    /// <summary>
    /// Main editor window: capture an avatar's blend shapes + bones into a card
    /// (embedded into a PNG), load cards back, apply them, and optionally generate
    /// a VRChat FX layer for runtime toggling.
    ///
    /// UI language: English / 中文 / 日本語 / 한국어 (persisted in EditorPrefs).
    /// Cover sources: auto placeholder / PNG file / Scene screenshot / QR code.
    /// Reset section: baseline capture/restore (never identity - it collapses rigs).
    /// </summary>
    public class ModelCardWindow : EditorWindow
    {
        public GameObject root;

        private string author = "";
        private string description = "";
        private bool includeAllShapes;
        private bool recordAllBones;
        private bool pixelBackup = true;
        private bool applyShapes = true;
        private bool applyBones = true;
        private CoverSource coverSource = CoverSource.Auto;
        private string qrText = "";
        private Vector2 scroll;

        private const string PrefDir = "VrcModelCard.lastDir";

        private static readonly string[] LangLabels = { "English", "中文", "日本語", "한국어" };

        // UI text snapshot - rebuilt only when the language changes, so OnGUI
        // does zero dictionary lookups or per-frame allocations.
        private sealed class Texts
        {
            public string windowTitle, subtitle, language, avatarRoot, author, description;
            public string captureOptions, includeAllShapes, recordAllBones, pixelBackup;
            public string coverSource, coverAuto, coverFile, coverScreenshot, coverQr, qrContent;
            public string[] coverLabels;
            public string actions, capturePng, captureJson, applyShapes, applyBones, loadApply, previewCard;
            public string genFx, genFxFromPng, reset, resetShapes, captureBaseline, restoreBaseline;
            public string selectRoot, notPng, captureFailed, cardSaved, jsonSaved, noCardFound;
            public string applyConfirm, applied, cardInfo, fxGenerated, fxFailed;
            public string resetShapesConfirm, baselineCaptured, baselineRestored, noBaseline;

            public Texts()
            {
                windowTitle = Loc.T("windowTitle");
                subtitle = Loc.T("subtitle");
                language = Loc.T("language");
                avatarRoot = Loc.T("avatarRoot");
                author = Loc.T("author");
                description = Loc.T("description");
                captureOptions = Loc.T("captureOptions");
                includeAllShapes = Loc.T("includeAllShapes");
                recordAllBones = Loc.T("recordAllBones");
                pixelBackup = Loc.T("pixelBackup");
                coverSource = Loc.T("coverSource");
                coverAuto = Loc.T("coverAuto");
                coverFile = Loc.T("coverFile");
                coverScreenshot = Loc.T("coverScreenshot");
                coverQr = Loc.T("coverQr");
                qrContent = Loc.T("qrContent");
                coverLabels = new[] { coverAuto, coverFile, coverScreenshot, coverQr };
                actions = Loc.T("actions");
                capturePng = Loc.T("capturePng");
                captureJson = Loc.T("captureJson");
                applyShapes = Loc.T("applyShapes");
                applyBones = Loc.T("applyBones");
                loadApply = Loc.T("loadApply");
                previewCard = Loc.T("previewCard");
                genFx = Loc.T("genFx");
                genFxFromPng = Loc.T("genFxFromPng");
                reset = Loc.T("reset");
                resetShapes = Loc.T("resetShapes");
                captureBaseline = Loc.T("captureBaseline");
                restoreBaseline = Loc.T("restoreBaseline");
                selectRoot = Loc.T("selectRoot");
                notPng = Loc.T("notPng");
                captureFailed = Loc.T("captureFailed");
                cardSaved = Loc.T("cardSaved");
                jsonSaved = Loc.T("jsonSaved");
                noCardFound = Loc.T("noCardFound");
                applyConfirm = Loc.T("applyConfirm");
                applied = Loc.T("applied");
                cardInfo = Loc.T("cardInfo");
                fxGenerated = Loc.T("fxGenerated");
                fxFailed = Loc.T("fxFailed");
                resetShapesConfirm = Loc.T("resetShapesConfirm");
                baselineCaptured = Loc.T("baselineCaptured");
                baselineRestored = Loc.T("baselineRestored");
                noBaseline = Loc.T("noBaseline");
            }
        }

        private Texts t;

        [MenuItem("Tools/VRC Model Card/Open Window %#m")]
        public static void Open()
        {
            var w = GetWindow<ModelCardWindow>(Loc.T("windowTitle"));
            w.minSize = new Vector2(460, 700);
            w.Show();
        }

        private void OnEnable()
        {
            t = new Texts();
            root = Selection.activeGameObject;
            titleContent = new GUIContent(t.windowTitle);
            Selection.selectionChanged += OnSelectionChanged;
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
        }

        private void OnSelectionChanged()
        {
            GameObject sel = Selection.activeGameObject;
            if (sel != root)
            {
                root = sel;
                Repaint();
            }
        }

        private string LastDir
        {
            get { return EditorPrefs.GetString(PrefDir, ""); }
            set { EditorPrefs.SetString(PrefDir, value); }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            // language switcher (English / 中文 / 日本語 / 한국어)
            int li = EditorGUILayout.Popup(t.language, (int)Loc.Current, LangLabels);
            if (li != (int)Loc.Current)
            {
                Loc.Set((UiLanguage)li);
                t = new Texts();
                titleContent = new GUIContent(t.windowTitle);
            }

            EditorGUILayout.LabelField(t.windowTitle, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(t.subtitle, EditorStyles.miniLabel);
            EditorGUILayout.Space();

            root = (GameObject)EditorGUILayout.ObjectField(t.avatarRoot, root, typeof(GameObject), true);

            EditorGUILayout.Space();
            author = EditorGUILayout.TextField(t.author, author);
            description = EditorGUILayout.TextArea(description, GUILayout.Height(44));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(t.captureOptions, EditorStyles.boldLabel);
            includeAllShapes = EditorGUILayout.Toggle(t.includeAllShapes, includeAllShapes);
            recordAllBones = EditorGUILayout.Toggle(t.recordAllBones, recordAllBones);
            pixelBackup = EditorGUILayout.Toggle(t.pixelBackup, pixelBackup);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(t.coverSource, EditorStyles.boldLabel);
            int cs = EditorGUILayout.Popup(t.coverSource, (int)coverSource, t.coverLabels);
            coverSource = (CoverSource)cs;
            if (coverSource == CoverSource.Qr)
            {
                qrText = EditorGUILayout.TextField(t.qrContent, qrText);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(t.actions, EditorStyles.boldLabel);
            if (GUILayout.Button(t.capturePng)) CaptureToPng();
            if (GUILayout.Button(t.captureJson)) CaptureToJson();
            EditorGUILayout.Space();
            applyShapes = EditorGUILayout.Toggle(t.applyShapes, applyShapes);
            applyBones = EditorGUILayout.Toggle(t.applyBones, applyBones);
            if (GUILayout.Button(t.loadApply)) LoadAndApply();
            if (GUILayout.Button(t.previewCard)) PreviewCard();
            EditorGUILayout.Space();
            if (GUILayout.Button(t.genFx)) GenerateFx();
            if (GUILayout.Button(t.genFxFromPng)) GenerateFxFromPng();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(t.reset, EditorStyles.boldLabel);
            if (GUILayout.Button(t.captureBaseline)) CaptureBaselineNow();
            if (GUILayout.Button(t.restoreBaseline)) RestoreBaselineNow();
            if (GUILayout.Button(t.resetShapes)) ResetShapes();

            EditorGUILayout.EndScrollView();
        }

        // ----------------------------------------------------------------- actions
        public void CaptureToPng()
        {
            if (!ValidateRoot()) return;
            var card = MakeCard();

            byte[] baseImage = null;
            switch (coverSource)
            {
                case CoverSource.File:
                    string baseImagePath = EditorUtility.OpenFilePanel(t.coverFile, LastDir, "png");
                    if (!string.IsNullOrEmpty(baseImagePath))
                    {
                        baseImage = File.ReadAllBytes(baseImagePath);
                        if (!PngCodec.IsPng(baseImage))
                        {
                            EditorUtility.DisplayDialog(t.windowTitle, t.notPng, "OK");
                            baseImage = null;
                        }
                        else
                        {
                            LastDir = Path.GetDirectoryName(baseImagePath);
                        }
                    }
                    break;
                case CoverSource.Screenshot:
                    if (!ModelCardIO.TryCaptureSceneView(out baseImage))
                    {
                        EditorUtility.DisplayDialog(t.windowTitle, t.captureFailed, "OK");
                        return;
                    }
                    break;
                case CoverSource.Qr:
                    string content = string.IsNullOrEmpty(qrText) ? QrCode.AutoSummary(card) : qrText;
                    baseImage = QrCode.RenderPng(content);
                    break;
                default: break; // Auto placeholder
            }

            string savePath = EditorUtility.SaveFilePanel("Save Model Card",
                LastDir, ModelCardIO.SanitizeFileName(root.name) + "_card.png", "png");
            if (string.IsNullOrEmpty(savePath)) return;
            LastDir = Path.GetDirectoryName(savePath);

            byte[] png = ModelCardIO.SaveCardToPng(card, baseImage, pixelBackup);
            File.WriteAllBytes(savePath, png);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(t.windowTitle,
                string.Format(t.cardSaved, Path.GetFileName(savePath), card.TotalShapes, card.bones.Count, png.Length / 1024f),
                "OK");
        }

        public void CaptureToJson()
        {
            if (!ValidateRoot()) return;
            var card = MakeCard();
            string savePath = EditorUtility.SaveFilePanel("Save Model Card (JSON)",
                LastDir, ModelCardIO.SanitizeFileName(root.name) + "_card.json", "json");
            if (string.IsNullOrEmpty(savePath)) return;
            LastDir = Path.GetDirectoryName(savePath);
            File.WriteAllText(savePath, JsonUtility.ToJson(card, true));
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(t.windowTitle, t.jsonSaved, "OK");
        }

        public void LoadAndApply()
        {
            if (!ValidateRoot()) return;
            string path = EditorUtility.OpenFilePanel("Load Model Card", LastDir, "png");
            if (string.IsNullOrEmpty(path)) return;
            LastDir = Path.GetDirectoryName(path);

            var card = ReadCard(path);
            if (card == null)
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.noCardFound, "OK");
                return;
            }

            string msg = string.Format(t.applyConfirm,
                card.avatarName, card.author, card.createdAt, card.TotalShapes, card.bones.Count, root.name);
            if (!EditorUtility.DisplayDialog(t.applyConfirm, msg, "Apply", "Cancel")) return;

            // remember the pre-apply state so "Restore Baseline" can undo the card
            if (!ModelCardIO.HasBaseline(root)) ModelCardIO.CaptureBaseline(root);

            int applied = ModelCardIO.Apply(root, card, applyShapes, applyBones);
            EditorUtility.DisplayDialog(t.windowTitle, string.Format(t.applied, applied), "OK");
        }

        public void PreviewCard()
        {
            string path = EditorUtility.OpenFilePanel("Preview Model Card (PNG)", LastDir, "png");
            if (string.IsNullOrEmpty(path)) return;
            LastDir = Path.GetDirectoryName(path);

            var card = ReadCard(path);
            if (card == null)
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.noCardFound, "OK");
                return;
            }
            EditorUtility.DisplayDialog("Model Card Info",
                string.Format(t.cardInfo,
                    card.avatarName, card.author, card.createdAt, card.description, card.TotalShapes, card.bones.Count),
                "OK");
        }

        public void GenerateFx()
        {
            if (!ValidateRoot()) return;
            var card = MakeCard();
            string error;
            if (ModelCardFxGenerator.Generate(root, card, out error))
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.fxGenerated, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(t.windowTitle, string.Format(t.fxFailed, error), "OK");
            }
        }

        public void GenerateFxFromPng()
        {
            if (!ValidateRoot()) return;
            string path = EditorUtility.OpenFilePanel("Generate FX Layer from Card (PNG)", LastDir, "png");
            if (string.IsNullOrEmpty(path)) return;
            LastDir = Path.GetDirectoryName(path);
            var card = ReadCard(path);
            if (card == null)
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.noCardFound, "OK");
                return;
            }
            string error;
            if (ModelCardFxGenerator.Generate(root, card, out error))
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.fxGenerated, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(t.windowTitle, string.Format(t.fxFailed, error), "OK");
            }
        }

        // ------------------------------------------------------------------ helpers
        private ModelCardData MakeCard()
        {
            var card = ModelCardIO.Capture(root, includeAllShapes, recordAllBones);
            card.author = author;
            card.description = description;
            return card;
        }

        private bool ValidateRoot()
        {
            if (root != null) return true;
            EditorUtility.DisplayDialog(t.windowTitle, t.selectRoot, "OK");
            return false;
        }

        private static ModelCardData ReadCard(string path)
        {
            try
            {
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonUtility.FromJson<ModelCardData>(File.ReadAllText(path));
                }
                return ModelCardIO.LoadCardFromPng(File.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ModelCard] Failed to read card: " + e.Message);
                return null;
            }
        }

        private void CaptureBaselineNow()
        {
            if (!ValidateRoot()) return;
            var card = ModelCardIO.CaptureBaseline(root);
            EditorUtility.DisplayDialog(t.windowTitle,
                string.Format(t.baselineCaptured, card.TotalShapes, card.bones.Count), "OK");
        }

        private void RestoreBaselineNow()
        {
            if (!ValidateRoot()) return;
            int restored = ModelCardIO.RestoreBaseline(root);
            if (restored < 0)
            {
                EditorUtility.DisplayDialog(t.windowTitle, t.noBaseline, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(t.windowTitle, string.Format(t.baselineRestored, restored), "OK");
            }
        }

        private void ResetShapes()
        {
            if (!ValidateRoot()) return;
            if (!EditorUtility.DisplayDialog(t.windowTitle, t.resetShapesConfirm, "Reset", "Cancel")) return;
            Undo.RegisterFullObjectHierarchyUndo(root, "Reset Blend Shapes");
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    smr.SetBlendShapeWeight(i, 0f);
                }
            }
            EditorUtility.SetDirty(root);
        }
    }
}
