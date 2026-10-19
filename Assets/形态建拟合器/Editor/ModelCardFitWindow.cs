using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Fit-from-image window: load a reference photo, optionally mark landmark
    /// pairs, and search the avatar's blend shape weights so its rendered
    /// silhouette matches the photo. The result can be saved as a regular card
    /// (PNG with the reference photo as cover, or JSON).
    /// </summary>
    public partial class ModelCardFitWindow : EditorWindow
    {
        public GameObject root;

        private Texture2D imageTex;
        private byte[] imageBytes;
        private int imgW, imgH;
        private SilhouetteMask.Result imageMask;
        private Texture2D renderPreview;
        private Camera previewCam;
        private LandmarkSet landmarks = new LandmarkSet();
        private float bgTolerance = 0.12f;
        private bool mirror;
        private bool useLandmarks = true;
        private int fitPasses = 3;
        private int maxRenders = 15000;
        private float landmarkHuberDeltaPixels = 6f;
        private float candidateProbeWeight = 25f;
        private bool rankCandidates = true;
        private bool useSceneCamera;
        private Camera sceneCamera;
        private Matrix4x4 lastSceneCameraTransform;
        private Matrix4x4 lastSceneCameraProjection;
        private bool hasSceneCameraSnapshot;
        private Vector2 scroll;
        private FitResult lastResult;
        private Texture2D fitBeforePreview, fitAfterPreview, fitDifferencePreview;
        private int comparisonMode;
        private bool enlargeEyes;
        private Rect eyePreviewUv = new Rect(0, .4f, 1, .4f);
        private int changedPreviewPixels;
        private float originalPixelScale = 1;
        private GameObject fittedRoot;
        private bool showResultPreview;
        private bool showWeightDetails;
        private string previewError;
        private struct WeightSnapshot
        {
            public SkinnedMeshRenderer renderer;
            public float[] weights;
        }
        [SerializeField] private bool photoOnly = true;
        [SerializeField] private PhotoAnnotation photoAnnotation = new PhotoAnnotation();
        [SerializeField] private int activePhotoPoint;
        private PhotoLandmarkTemplate photoTemplate;
        private SkinnedMeshRenderer templateRenderer;
        private GameObject boundRoot;
        private string templateStatus = "";
        private bool showModelPreview;
        private bool advancedOptions, annotationTools, exportOptions;
        private int workflowPage;
        private bool fitEyelids = true, fitImageRoll = true;
        private bool fitDenseRegions = true;
        private bool showProjectionOverlay;

        [MenuItem("形态键拟合/图片拟合", false, 10)]
        public static void Open()
        {
            var w = GetWindow<ModelCardFitWindow>("图片拟合");
            w.minSize = new Vector2(760, 640);
            w.Show();
        }

        private void OnEnable()
        {
            if (root == null) root = Selection.activeGameObject;
            Undo.undoRedoPerformed += PhotoUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= PhotoUndoRedo;
            EndPhotoDrag(true);
            EndPhotoPan();EndPhotoTraceDrag(false);
            CancelPhotoAuto();
            CleanupRender();
            ClearFitResult();
        }

        private void Update()
        {
            PollPhotoAuto();
            if (!useSceneCamera || previewCam == null || renderPreview == null) return;
            Camera source = GetSourceCamera();
            if (source == null) return;
            if (!hasSceneCameraSnapshot || source.transform.localToWorldMatrix != lastSceneCameraTransform || source.projectionMatrix != lastSceneCameraProjection)
            {
                AvatarFitRenderer.CopyCameraSettings(source, previewCam);
                AvatarFitRenderer.SetRenderAspect(previewCam, imgW, imgH);
                renderPreview = AvatarFitRenderer.RenderWithAvatar(root, previewCam, imgW, imgH, renderPreview);
                lastSceneCameraTransform = source.transform.localToWorldMatrix;
                lastSceneCameraProjection = source.projectionMatrix;
                Repaint();
            }
        }

        private Camera GetSourceCamera()
        {
            if (!useSceneCamera) return null;
            if (sceneCamera != null) return sceneCamera;
            return SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
        }

        private void CleanupRender()
        {
            if (renderPreview != null) { UnityEngine.Object.DestroyImmediate(renderPreview); renderPreview = null; }
            if (previewCam != null) { UnityEngine.Object.DestroyImmediate(previewCam.gameObject); previewCam = null; }
        }

        private void OnGUI()
        {
            titleContent = new GUIContent("图片拟合");
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("图片拟合", EditorStyles.largeLabel);
            EditorGUILayout.LabelField("选择图片，只标图片上的特征，再查看拟合前后效果。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            var nextRoot = (GameObject)EditorGUILayout.ObjectField("待调整模型", root, typeof(GameObject), true);
            if (nextRoot != root) { root = nextRoot; boundRoot = null; photoProfileAsset = null; CleanupRender(); ClearFitResult(); landmarks = new LandmarkSet(); }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(imageTex == null ? "选择参考图片…" : "更换参考图片…", GUILayout.Height(26))) LoadImage();
            EditorGUILayout.LabelField(imageTex == null ? "尚未选择图片" : imageTex.width + " × " + imageTex.height, EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            if (photoOnly)
            {
                useLandmarks = true; mirror = false; useSceneCamera = false;
                if (photoTemplate == null || boundRoot != root || photoCoordinatesDirty) BindPhotoTemplate();
                DrawPhotoModelBinding();
            }
            workflowPage = GUILayout.Toolbar(workflowPage, new[] { "① 标记图片", "② 查看结果" }, GUILayout.Height(28));
            EditorGUILayout.Space(6);
            if (workflowPage == 0)
            {
                if (photoOnly) DrawPhotoControls();
                DrawPhotoAdvanced();
                using (new EditorGUI.DisabledScope(imageTex == null || root == null || HasPendingPhotoTrace() || (photoOnly && (templateRenderer == null || landmarks.Count < 4))))
                    if (GUILayout.Button(HasPendingPhotoTrace()?"先采用或清除描边草稿，再开始拟合":"开始拟合并预览", GUILayout.Height(34))) RunFit();
                if (!string.IsNullOrEmpty(previewError)) EditorGUILayout.HelpBox(previewError, MessageType.Error);
                if (workflowPage == 0) DrawReferenceImage();
            }
            else
            {
                if (lastResult == null) EditorGUILayout.HelpBox("尚无拟合结果。先在“标记图片”中选择图片、标注特征，再开始拟合。", MessageType.Info);
                else
                {
                    DrawFitResultPreview();
                    exportOptions = EditorGUILayout.Foldout(exportOptions, "兼容导出（模型卡片 / JSON）", true);
                    if (exportOptions)
                    {
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button("导出模型卡片图片…")) SavePng();
                        if (GUILayout.Button("导出权重 JSON…")) SaveJson();
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawPhotoAdvanced()
        {
            advancedOptions = EditorGUILayout.Foldout(advancedOptions, "高级设置", true);
            if (!advancedOptions) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (photoProfile == null)
            {
                fitEyelids = EditorGUILayout.Toggle("拟合眼睑", fitEyelids);
                fitDenseRegions = EditorGUILayout.Toggle("拟合已标注的眉形、嘴型和脸廓", fitDenseRegions);
            }
            fitImageRoll = EditorGUILayout.Toggle("估计图片倾斜", fitImageRoll);
            fitPasses = Mathf.Clamp(EditorGUILayout.IntField("拟合轮数", fitPasses), 1, 10);
            maxRenders = Mathf.Clamp(EditorGUILayout.IntField("每初值评估上限", maxRenders), 100, 50000);
            landmarkHuberDeltaPixels = EditorGUILayout.Slider("异常点容忍度（像素）", landmarkHuberDeltaPixels, 1f, 20f);
            candidateProbeWeight = EditorGUILayout.Slider("形态键探测幅度", candidateProbeWeight, 5f, 50f);
            if (photoProfile == null) rankCandidates = EditorGUILayout.Toggle("优先拟合可观测形态键", rankCandidates);
            else EditorGUILayout.LabelField("按当前标点的二维形变响应筛选坐标档案中的候选键；弱响应键固定当前权重。", EditorStyles.wordWrappedMiniLabel);
            EditorGUI.BeginChangeCheck();
            bool legacy = EditorGUILayout.Toggle("旧版双侧标点模式（兼容）", !photoOnly);
            if (EditorGUI.EndChangeCheck()) { photoOnly = !legacy; landmarks = new LandmarkSet(); ClearFitResult(); if (photoOnly) BindPhotoTemplate(); }
            if (!photoOnly)
            {
                mirror = EditorGUILayout.Toggle("镜像模型预览", mirror);
                useSceneCamera = EditorGUILayout.Toggle("跟随场景相机", useSceneCamera);
                if (useSceneCamera) sceneCamera = (Camera)EditorGUILayout.ObjectField("参考相机（空为场景视图）", sceneCamera, typeof(Camera), true);
                useLandmarks = EditorGUILayout.Toggle("使用对应点", useLandmarks);
                bgTolerance = EditorGUILayout.Slider("背景容差", bgTolerance, .02f, .5f);
            }
            using (new EditorGUI.DisabledScope(root == null || imageTex == null))
                if (GUILayout.Button("刷新模型预览")) RefreshPreview();
            EditorGUILayout.HelpBox("支持已构建并检查模型坐标的模型；当前为单个脸部网格、0–100 权重。只估计正面对齐与部分形态键；深度、三维转头、发型和材质仍需人工判断。", MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private void DrawReferenceImage()
        {
            if (imageTex == null) { EditorGUILayout.HelpBox("选择参考图片后，即可在图片上点击标记。", MessageType.Info); return; }
            EditorGUILayout.Space(6);
            bool twoPanes = renderPreview != null && (!photoOnly || showModelPreview);
            float paneW = Mathf.Max(120f, (position.width - 40f) / (twoPanes ? 2 : 1));
            float paneH = paneW * imageTex.height / Mathf.Max(imageTex.width, 1);
            paneH = Mathf.Min(paneH, Mathf.Max(260, position.height * .49f));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField("参考图片 · 轮廓与区域编辑", EditorStyles.miniLabel);
            DrawPhotoViewControls();
            DrawPhotoImageViewport(GUILayoutUtility.GetRect(paneW, paneH));
            EditorGUILayout.EndVertical();
            if (twoPanes)
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(photoOnly ? "模型预览 · 无需标记" : "旧版模型对应点", EditorStyles.miniLabel);
                Rect p = GetTextureRect(GUILayoutUtility.GetRect(paneW, paneH), renderPreview.width, renderPreview.height);
                GUI.DrawTexture(p, renderPreview, ScaleMode.StretchToFill);
                HandleClick(p, renderPreview.width, renderPreview.height, false, Event.current);
                DrawLandmarksOnPane(p, renderPreview.width, renderPreview.height, true);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
            if (!photoOnly && GUILayout.Button("清除旧版对应点")) landmarks = new LandmarkSet();
        }

        // --------------------------------------------------------------- actions
        private void LoadImage()
        {
            string path = EditorUtility.OpenFilePanel("选择参考图片", "", "png;jpg;jpeg");
            if (string.IsNullOrEmpty(path)) return;
            LoadImagePath(path);
        }

        public void LoadImagePath(string path)
        {
            EndPhotoDrag(true);
            ResetPhotoView();ResetPhotoTraces();
            CancelPhotoAuto(); autoCandidates = null; autoStatus = "";
            imageBytes = File.ReadAllBytes(path);
            if (imageTex != null) { UnityEngine.Object.DestroyImmediate(imageTex); imageTex = null; }
            imageTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!imageTex.LoadImage(imageBytes))
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitNoImage"), "OK");
                imageTex = null;
                return;
            }
            CleanupRender();
            landmarks = new LandmarkSet();
            photoAnnotation = new PhotoAnnotation { imageWidth = imageTex.width, imageHeight = imageTex.height,
                imageSha256 = PhotoLandmarkTemplate.Hash(imageBytes) };
            activePhotoPoint = 0;
            workflowPage = 0;
            BindPhotoTemplate();
            ClearFitResult();
            imageMask = SilhouetteMask.Extract(imageTex, 256, bgTolerance);
            imgW = imageMask.width;
            imgH = imageMask.height;
            if (root != null) RefreshPreview();
        }

        private void RefreshPreview()
        {
            if (root == null)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("selectRoot"), "OK");
                return;
            }
            if (imageTex == null)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitNoImage"), "OK");
                return;
            }
            imageMask = SilhouetteMask.Extract(imageTex, 256, bgTolerance);
            imgW = imageMask.width;
            imgH = imageMask.height;
            if (!imageMask.hasForeground && !photoOnly)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitNoForeground"), "OK");
                return;
            }
            CleanupRender();
            hasSceneCameraSnapshot = false;
            if (!AvatarFitRenderer.RenderAvatar(root, imgW, imgH, out renderPreview, out previewCam, mirror, GetSourceCamera()))
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitRenderFailed"), "OK");
                return;
            }
            Camera source = GetSourceCamera();
            if (source != null)
            {
                lastSceneCameraTransform = source.transform.localToWorldMatrix;
                lastSceneCameraProjection = source.projectionMatrix;
                hasSceneCameraSnapshot = true;
            }
            if (photoOnly && templateRenderer != null)
            {
                PhotoLandmarkTemplate.ConfigureCamera(photoTemplate, templateRenderer, previewCam, imgW, imgH);
                renderPreview = AvatarFitRenderer.RenderWithAvatar(root, previewCam, imgW, imgH, renderPreview);
            }
            if (photoOnly) RebuildPhotoLandmarks(); else landmarks = new LandmarkSet();
            ClearFitResult();
            Repaint();
        }

        private void RunFit()
        {
            if(HasPendingPhotoTrace()){previewError="还有未采用的轮廓草稿，请先采用或清除后再拟合。";return;}
            if (root == null) { EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("selectRoot"), "OK"); return; }
            if (imageTex == null) { EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitNoImage"), "OK"); return; }
            if (photoOnly) { BindPhotoTemplate(); if (templateRenderer == null) { previewError = templateStatus; return; } }
            if (renderPreview == null) RefreshPreview();
            if (renderPreview == null) return;

            if (photoOnly)
            {
                if (templateRenderer == null) return;
                RebuildPhotoLandmarks();
                bool eye = photoAnnotation.points.Exists(p => p.state == "marked" && p.id.Contains("eye"));
                bool lowerFace = photoAnnotation.points.Exists(p => p.state == "marked" && (p.id == "chin" || p.id.StartsWith("mouth")));
                if (landmarks.Count < 4 || !eye || !lowerFace)
                {
                    previewError = "至少标记 4 个可见点，包含眼部和嘴角或下巴；遮挡点可跳过。"; return;
                }
            }
            if (useLandmarks && landmarks.Count < 1)
            {
                if (!EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"),
                    Loc.T("fitLandmarkHint") + "\n\n" + Loc.T("fitNoLandmarksContinue"), "Continue", "Cancel"))
                    return;
            }

            var settings = new FitSettings
            {
                recordUndo = false,
                photoOnlyTemplate = photoOnly,
                stabilizePhotoFit=photoOnly,
                photoAnnotation=photoOnly?JsonUtility.FromJson<PhotoAnnotation>(JsonUtility.ToJson(photoAnnotation)):null,
                photoProfile = photoOnly ? photoProfile : null,
                photoTemplate = photoOnly ? photoTemplate : null,
                fitEyelids = fitEyelids, fitImageRoll = fitImageRoll,
                fitDenseRegions = fitDenseRegions,
                probes = photoOnly ? new float[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 } : new float[] { -50, 0, 50, 100 },
                passes = fitPasses,
                maxRenders = maxRenders,
                useLandmarks = useLandmarks,
                bgTolerance = bgTolerance,
                landmarkHuberDeltaPixels = this.landmarkHuberDeltaPixels,
                candidateProbeWeight = this.candidateProbeWeight,
                rankCandidates = this.rankCandidates,
            };

            bool cancelled = false;
            ClearFitResult();
            var snapshots = new System.Collections.Generic.List<WeightSnapshot>();
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                var weights = new float[renderer.sharedMesh.blendShapeCount];
                for (int i = 0; i < weights.Length; i++) weights[i] = renderer.GetBlendShapeWeight(i);
                snapshots.Add(new WeightSnapshot { renderer = renderer, weights = weights });
            }
            int previewHeight = 768;
            int previewWidth = Mathf.Clamp(Mathf.RoundToInt(previewHeight * (float)imgW / Mathf.Max(imgH, 1)), 1, 1536);
            try
            {
                eyePreviewUv = GetEyePreviewUv();
                fittedRoot = root;
                originalPixelScale = (float)imageTex.width / Mathf.Max(imgW, 1);
                fitBeforePreview = FitPreviewCapture.Capture(root, previewCam, previewWidth, previewHeight);
                lastResult = FittingOptimizer.Fit(root, imageTex, useLandmarks ? landmarks : null, settings,
                    (msg, frac) => { cancelled = EditorUtility.DisplayCancelableProgressBar(Loc.T("fitFitting"), msg, frac); },
                    () => cancelled);
                // Capture while fitted weights (including exact zero values) are
                // still present. Do not reapply a sparse exported card here.
                fitAfterPreview = FitPreviewCapture.Capture(root, previewCam, previewWidth, previewHeight);
                BuildDifferencePreview();
                if (photoOnly && photoProfile != null) photoFitRecord = PhotoFitResultActions.Capture(root, templateRenderer, photoProfile, photoAnnotation, settings, lastResult, originalPixelScale);
                showResultPreview = true; workflowPage = 1;
                scroll = Vector2.zero;
            }
            catch (Exception e)
            {
                string key = e.Message;
                string msg = key == "NoForeground" ? Loc.T("fitNoForeground")
                    : key == "NoShapes" ? Loc.T("fitNoShapes")
                    : key == "RenderFailed" ? Loc.T("fitRenderFailed")
                    : e.Message;
                ClearFitResult();
                previewError = msg;
            }
            finally
            {
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.renderer == null) continue;
                    for (int i = 0; i < snapshot.weights.Length; i++) snapshot.renderer.SetBlendShapeWeight(i, snapshot.weights[i]);
                }
                EditorUtility.ClearProgressBar();
            }

            Repaint();
        }

        private void ClearFitResult()
        {
            photoFitRecord = null; photoActionStatus = null;
            lastResult = null; showResultPreview = false; previewError = null;
            if (fitBeforePreview != null) UnityEngine.Object.DestroyImmediate(fitBeforePreview);
            if (fitAfterPreview != null) UnityEngine.Object.DestroyImmediate(fitAfterPreview);
            if (fitDifferencePreview != null) UnityEngine.Object.DestroyImmediate(fitDifferencePreview);
            fitBeforePreview = null; fitAfterPreview = null; fitDifferencePreview = null;
            changedPreviewPixels = 0; fittedRoot = null;
        }

        private void DrawFitResultPreview()
        {
            if (!string.IsNullOrEmpty(previewError)) EditorGUILayout.HelpBox(previewError, MessageType.Error);
            if (lastResult == null || fitAfterPreview == null) return;
            EditorGUILayout.LabelField(lastResult.cancelled ? "拟合已中止 · 以下为候选结果" : "拟合结果", EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(lastResult.objectiveVersion))
            {
                EditorGUILayout.LabelField("部位均衡二维偏差",string.Format("{0:0.##} → {1:0.##} 原图像素",lastResult.initialBalancedRmsePixels,lastResult.balancedRmsePixels));
                EditorGUILayout.LabelField("优化代价",string.Format("二维 {0:0.###} + 权重先验 {1:0.###}",lastResult.dataLoss,lastResult.priorLoss));
                EditorGUILayout.HelpBox("描边按同部位分段轮廓匹配，各部位等权；权重以本次拟合开始值为中心约束。模型轮廓仍为锚点折线近似；单侧或遮挡部位不能保证恢复。",MessageType.Info);
            }
            else if (!float.IsNaN(lastResult.finalLandmarkRmse))
                EditorGUILayout.LabelField("图片点误差", string.Format("{0:0.##} → {1:0.##} 像素", lastResult.initialLandmarkRmse * originalPixelScale, lastResult.finalLandmarkRmse * originalPixelScale));
            comparisonMode = GUILayout.Toolbar(comparisonMode, new[] { "前后对比", "拟合前", "拟合后", "差异" });
            enlargeEyes = EditorGUILayout.Toggle("放大眼部", enlargeEyes);
            Rect uv = enlargeEyes ? eyePreviewUv : new Rect(0, 0, 1, 1);
            float available = Mathf.Max(100, position.width - 48);
            float paneWidth = comparisonMode == 0 ? available / 2 : available;
            float paneHeight = Mathf.Min(400, paneWidth * fitAfterPreview.height * uv.height / (fitAfterPreview.width * uv.width));
            EditorGUILayout.BeginHorizontal();
            if (comparisonMode == 0 || comparisonMode == 1) DrawResultTexture("拟合前", fitBeforePreview, paneWidth, paneHeight, uv);
            if (comparisonMode == 0 || comparisonMode == 2) DrawResultTexture("拟合后", fitAfterPreview, paneWidth, paneHeight, uv);
            if (comparisonMode == 3) DrawResultTexture("差异高亮", fitDifferencePreview, paneWidth, paneHeight, uv);
            EditorGUILayout.EndHorizontal();
            bool changedWeight = lastResult.weightChanges.Exists(c => Mathf.Abs(c.after - c.before) > .01f);
            if (!changedWeight) EditorGUILayout.HelpBox("没有找到更好的权重，前后相同；这不代表已匹配照片。", MessageType.Warning);
            else if (lastResult.weightChanges.Exists(c => c.atBoundary)) EditorGUILayout.HelpBox("部分参数已达到允许范围，仍可能与照片存在差异。", MessageType.Warning);
            if (photoFitRecord != null) DrawPhotoResultActions();
            else using (new EditorGUI.DisabledScope(fittedRoot != root || lastResult.cancelled))
                if (GUILayout.Button("应用到场景（可撤销）", GUILayout.Height(30))) ApplyPreviewToScene();
            showWeightDetails = EditorGUILayout.Foldout(showWeightDetails, "参数与稳定性详情", true);
            if (showWeightDetails)
            {
                EditorGUILayout.LabelField("图片倾斜", lastResult.imageRollDegrees.ToString("0.##") + "°（非三维姿态）");
                EditorGUILayout.LabelField("多初值", lastResult.startLosses.Count + " 次；按"+(string.IsNullOrEmpty(lastResult.objectiveVersion)?"二维损失":"二维＋先验总目标")+"选第 " + (lastResult.selectedStart + 1) + " 次，不保证全局最优");
                EditorGUILayout.LabelField("预览变化像素", changedPreviewPixels.ToString());
                for (int i=0;i<lastResult.weightChanges.Count;i++)
                {
                    var change=lastResult.weightChanges[i];float low=change.after,high=change.after;
                    foreach(var values in lastResult.startFinalWeights){low=Mathf.Min(low,values[i]);high=Mathf.Max(high,values[i]);}
                    EditorGUILayout.LabelField(change.name, string.Format("{0:0.#} → {1:0.#}{2}；多初值 {3:0.#}–{4:0.#}", change.before, change.after, change.atBoundary ? " [触边]" : "", low, high));
                }
                EditorGUILayout.LabelField("眉形、嘴型和脸廓依赖对应标注数量；深度、发型和材质仍需人工调整。多初值范围不是置信区间。", EditorStyles.wordWrappedMiniLabel);
                DrawCandidateSummary();
            }
        }

        private static void DrawResultTexture(string label, Texture2D texture, float width, float height, Rect uv)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(width));
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            Rect rect = GUILayoutUtility.GetRect(width, height);
            if (texture != null)
                GUI.DrawTextureWithTexCoords(GetTextureRect(rect, Mathf.RoundToInt(texture.width * uv.width), Mathf.RoundToInt(texture.height * uv.height)), texture, uv);
            EditorGUILayout.EndVertical();
        }

        private Rect GetEyePreviewUv()
        {
            if (!photoOnly || templateRenderer == null || photoTemplate == null) return new Rect(0, 0, 1, 1);
            var mesh = new Mesh();
            try
            {
                templateRenderer.BakeMesh(mesh);
                Vector3[] vertices = mesh.vertices;
                Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
                foreach (var p in photoTemplate.points)
                {
                    if (!p.id.Contains("eye")) continue;
                    var a = new LandmarkSet.Anchor { vertexIndices = p.vertices, vertexWeights = p.weights };
                    Vector3 uv = previewCam.WorldToViewportPoint(templateRenderer.transform.TransformPoint(a.Position(vertices)));
                    min = Vector2.Min(min, uv); max = Vector2.Max(max, uv);
                }
                float padX = Mathf.Max(.04f, (max.x - min.x) * .12f), padY = Mathf.Max(.08f, (max.y - min.y) * .6f);
                return Rect.MinMaxRect(Mathf.Clamp01(min.x - padX), Mathf.Clamp01(min.y - padY), Mathf.Clamp01(max.x + padX), Mathf.Clamp01(max.y + padY));
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        private void BuildDifferencePreview()
        {
            Color32[] before = fitBeforePreview.GetPixels32(), after = fitAfterPreview.GetPixels32();
            var pixels = new Color32[before.Length]; changedPreviewPixels = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int d = Mathf.Max(Mathf.Abs(before[i].r - after[i].r), Mathf.Max(Mathf.Abs(before[i].g - after[i].g), Mathf.Abs(before[i].b - after[i].b)));
                if (d > 0) changedPreviewPixels++;
                pixels[i] = new Color32((byte)Mathf.Min(255, d * 4), (byte)Mathf.Min(255, d), 0, 255);
            }
            fitDifferencePreview = new Texture2D(fitBeforePreview.width, fitBeforePreview.height, TextureFormat.RGBA32, false);
            fitDifferencePreview.SetPixels32(pixels); fitDifferencePreview.Apply();
        }

        private void ApplyPreviewToScene()
        {
            if (lastResult == null || lastResult.cancelled || fittedRoot != root) return;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            // Use full result values, including zero, rather than a sparse card.
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Apply fitted preview");
            foreach (var renderer in renderers)
            {
                if (renderer.sharedMesh == null) continue;
                if (photoOnly && renderer != templateRenderer) continue;
                Undo.RecordObject(renderer, "Apply fitted preview");
                foreach (var change in lastResult.weightChanges)
                {
                    if (change.rendererPath != ModelCardIO.GetRelativePath(root.transform, renderer.transform)) continue;
                    int index = renderer.sharedMesh.GetBlendShapeIndex(change.name);
                    if (index >= 0) renderer.SetBlendShapeWeight(index, change.after);
                }
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            Undo.CollapseUndoOperations(undo);
            SceneView.RepaintAll();
        }

        private void DrawCandidateSummary()
        {
            if (lastResult == null || lastResult.candidates == null || lastResult.candidates.Count == 0) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.T("fitCandidateTitle"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Loc.T("fitCandidateHint"), EditorStyles.wordWrappedMiniLabel);
            int count = photoProfile != null ? lastResult.candidates.Count : Mathf.Min(12, lastResult.candidates.Count);
            if (photoProfile != null)
                EditorGUILayout.LabelField("以下为原图像素中的二维响应，只用于筛选，不是置信度；弱响应键保留原值。当前模型的稳定性需单独验证。", EditorStyles.wordWrappedMiniLabel);
            for (int i = 0; i < count; i++)
            {
                var c = lastResult.candidates[i];
                EditorGUILayout.LabelField(string.Format("{0}. {1}  {2:0.###} px  {3}", i + 1, c.name, c.influencePixels, c.selected ? Loc.T("fitCandidate") : Loc.T("fitCandidateLow")), EditorStyles.miniLabel);
                if (photoProfile != null && !c.selected) EditorGUILayout.LabelField(c.reason, EditorStyles.wordWrappedMiniLabel);
            }
            if (lastResult.candidates.Count > count)
                EditorGUILayout.LabelField(string.Format(Loc.T("fitCandidateMore"), lastResult.candidates.Count - count), EditorStyles.miniLabel);
            if (GUILayout.Button(Loc.T("fitCandidateSave"))) SaveCandidateAudit();
        }

        private void SaveCandidateAudit()
        {
            if (lastResult == null || lastResult.candidates == null) return;
            string path = EditorUtility.SaveFilePanel("Save Candidate Audit", "", "shape_candidates.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            var wrapper = new CandidateAudit { avatar = root != null ? root.name : "", createdAt = DateTime.Now.ToString("o"), candidates = lastResult.candidates };
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
            EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitCandidateSaved"), "OK");
        }

        [Serializable]
        private class CandidateAudit
        {
            public string avatar;
            public string createdAt;
            public System.Collections.Generic.List<ShapeCandidate> candidates;
        }

        private void SavePng()
        {
            if (lastResult == null || lastResult.card == null || root == null) return;
            if (lastResult.card.TotalShapes == 0)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"),
                    "未保存：本次拟合没有产生非零形态键，因此没有可应用的拟合内容。请调整照片/特征点后重新拟合。", "OK");
                return;
            }
            string savePath = EditorUtility.SaveFilePanel("Save Fitted Card", "",
                ModelCardIO.SanitizeFileName(root.name) + "_fit_card.png", "png");
            if (string.IsNullOrEmpty(savePath)) return;
            byte[] png = ModelCardIO.SaveCardToPng(lastResult.card, imageBytes, true);
            // Verify the exact bytes before reporting success. This prevents a
            // plain screenshot or a post-save rewrite from being mistaken for
            // a loadable fitted card.
            if (ModelCardIO.LoadCardFromPng(png) == null)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"),
                    "保存失败：生成的 PNG 未包含可读取的拟合卡片数据。请保留 PNG 格式并重试。", "OK");
                return;
            }
            File.WriteAllBytes(savePath, png);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitSaved"), "OK");
        }

        private void SaveJson()
        {
            if (lastResult == null || lastResult.card == null || root == null) return;
            if (lastResult.card.TotalShapes == 0)
            {
                EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"),
                    "未保存：本次拟合没有产生非零形态键，因此没有可应用的拟合内容。请调整照片/特征点后重新拟合。", "OK");
                return;
            }
            string savePath = EditorUtility.SaveFilePanel("Save Fitted Card (JSON)", "",
                ModelCardIO.SanitizeFileName(root.name) + "_fit_card.json", "json");
            if (string.IsNullOrEmpty(savePath)) return;
            File.WriteAllText(savePath, JsonUtility.ToJson(lastResult.card, true));
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitSaved"), "OK");
        }

        private void BindPhotoTemplate()
        {
            boundRoot = root;
            photoCoordinatesDirty = false;
            templateRenderer = null;
            photoProfile = null;
            CleanupRender(); ClearFitResult();
            try
            {
                photoTemplate = PhotoDetailLandmarks.Expand(PhotoLandmarkTemplate.Load());
                photoAnnotation.templateId = photoTemplate.id;
                if (root == null) throw new InvalidOperationException("先选择场景中的模型根对象。");
                var selected = photoProfileAsset == null ? null : CommonCoordinateProfileStore.LoadProfile(AssetDatabase.GetAssetPath(photoProfileAsset));
                photoProfile = GenericPhotoModel.Bind(root, selected, out templateRenderer);
                photoTemplate = GenericPhotoModel.Template(photoProfile);
                templateStatus = root.name + " · " + templateRenderer.sharedMesh.name + " · "+photoTemplate.points.Length+" 个可选点（含 38 个近似细分点） / " + photoProfile.keys.Length + " 个候选键";
            }
            catch (Exception e) { templateRenderer = null; photoProfile = null; templateStatus = e.Message; }
            RebuildPhotoLandmarks();
        }

        private void RebuildPhotoLandmarks()
        {
            if (!photoOnly) return;
            observedAnnotationJson = JsonUtility.ToJson(photoAnnotation);
            landmarks = photoTemplate != null && templateRenderer != null && photoAnnotation != null
                ? photoTemplate.Build(templateRenderer, photoAnnotation, Mathf.Max(imgW, 2), Mathf.Max(imgH, 2)) : new LandmarkSet();
        }

        private void DrawPhotoControls()
        {
            DrawPhotoAutoControls();
            DrawManualPhotoEditor();
        }

        private void SelectPhotoRegion(string token)
        {
            int fallback = -1;
            for (int i = 0; i < photoTemplate.points.Length; i++)
            {
                if (!photoTemplate.points[i].id.Contains(token)) continue;
                if (fallback < 0) fallback = i;
                var mark = photoAnnotation.points.Find(p => p.id == photoTemplate.points[i].id);
                if (mark == null || mark.state == "unmarked") { activePhotoPoint = i; return; }
            }
            if (fallback >= 0) activePhotoPoint = fallback;
        }

        private void SkipEye(string prefix)
        {
            if (photoTemplate == null) return;
            foreach (var definition in photoTemplate.points)
            {
                if (!definition.id.StartsWith(prefix)) continue;
                var mark = photoAnnotation.points.Find(p => p.id == definition.id);
                photoAnnotation.Set(definition.id, mark != null ? mark.x : 0, mark != null ? mark.y : 0, "skipped");
            }
            RebuildPhotoLandmarks(); ClearFitResult();
        }

        private void MarkPhotoPoint(float x, float y)
        {
            Undo.RecordObject(this, "补标图片特征");
            photoAnnotation.templateId = photoTemplate.id;
            photoAnnotation.Set(photoTemplate.points[activePhotoPoint].id, x, y, "marked");
            AdvancePhotoPoint(); RebuildPhotoLandmarks(); ClearFitResult();
        }

        private void SetPhotoPointState(string state)
        {
            Undo.RecordObject(this, "更改图片标注状态");
            photoAnnotation.templateId = photoTemplate.id;
            string id = photoTemplate.points[activePhotoPoint].id;
            var old = photoAnnotation.points.Find(p => p.id == id);
            photoAnnotation.Set(id, old != null ? old.x : 0, old != null ? old.y : 0, state);
            if (state == "skipped") AdvancePhotoPoint();
            RebuildPhotoLandmarks(); ClearFitResult();
        }

        private void AdvancePhotoPoint()
        {
            if(photoEditMode==2)
            {
                var indices=VisiblePhotoPointIndices();int position=Array.IndexOf(indices,activePhotoPoint);
                for(int offset=1;offset<=indices.Length;offset++)
                {
                    int index=indices[(Mathf.Max(position,0)+offset)%indices.Length];
                    var p=photoAnnotation.points.Find(m=>m.id==photoTemplate.points[index].id);
                    if(p==null||p.state=="unmarked"){activePhotoPoint=index;return;}
                }
                return;
            }
            for (int offset = 1; offset <= photoTemplate.points.Length; offset++)
            {
                int i = (activePhotoPoint + offset) % photoTemplate.points.Length;
                var mark = photoAnnotation.points.Find(p => p.id == photoTemplate.points[i].id);
                if (mark == null || mark.state == "unmarked") { activePhotoPoint = i; return; }
            }
        }

        private void SavePhotoAnnotations()
        {
            string path = EditorUtility.SaveFilePanel("保存图片标注", "", "photo_landmarks.json", "json");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, JsonUtility.ToJson(photoAnnotation, true));
        }

        private void LoadPhotoAnnotations()
        {
            string path = EditorUtility.OpenFilePanel("载入图片标注", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var candidate = JsonUtility.FromJson<PhotoAnnotation>(File.ReadAllText(path));
                if (candidate == null) throw new InvalidOperationException("标注文件无效。");
                candidate.Validate(photoTemplate, PhotoLandmarkTemplate.Hash(imageBytes), imageTex.width, imageTex.height);
                photoAnnotation = candidate; RebuildPhotoLandmarks(); ClearFitResult();
            }
            catch (Exception e) { EditorUtility.DisplayDialog("载入失败", e.Message, "OK"); }
        }

        private void UpdateTemplatePreviewPoints()
        {
            if (templateRenderer == null || previewCam == null) return;
            var mesh = new Mesh();
            try
            {
                templateRenderer.BakeMesh(mesh);
                Vector3[] vertices = mesh.vertices;
                foreach (var anchor in landmarks.renderAnchors)
                {
                    Vector3 point = previewCam.WorldToViewportPoint(templateRenderer.transform.TransformPoint(anchor.Position(vertices)));
                    anchor.renderPixel = new Vector2(point.x * renderPreview.width, point.y * renderPreview.height);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        // -------------------------------------------------------- landmark clicks
        // Use the same aspect-fitted content rect for drawing, picking and
        // markers. GUILayout's full pane can also contain letterbox margins.
        private static Rect GetTextureRect(Rect pane, int texW, int texH)
        {
            if (pane.width <= 0f || pane.height <= 0f || texW <= 0 || texH <= 0)
                return new Rect(pane.position, Vector2.zero);
            float scale = Mathf.Min(pane.width / texW, pane.height / texH);
            Vector2 size = new Vector2(texW * scale, texH * scale);
            return new Rect(pane.center - size * 0.5f, size);
        }

        private void HandleClick(Rect r, int texW, int texH, bool isImage, Event evt)
        {
            if (!useLandmarks) return;
            if (photoOnly && (!isImage || photoTemplate == null || imageTex == null)) return;
            if (r.width <= 0f || r.height <= 0f) return;
            if (evt == null || evt.type != EventType.MouseDown || evt.button != 0 || !r.Contains(evt.mousePosition)) return;

            float fx = (evt.mousePosition.x - r.xMin) / r.width;
            float fy = (evt.mousePosition.y - r.yMin) / r.height;
            int px = Mathf.Clamp(Mathf.FloorToInt(fx * texW), 0, texW - 1);
            int py = Mathf.Clamp(Mathf.FloorToInt(fy * texH), 0, texH - 1);

            if (photoOnly && isImage)
            {
                MarkPhotoPoint((float)px / Mathf.Max(texW - 1, 1), (float)py / Mathf.Max(texH - 1, 1));
                evt.Use(); Repaint(); return;
            }
            if (isImage)
            {
                // GUI clicks use top-left origin; Unity projection uses
                // bottom-left origin internally.
                float fitX = texW > 1 ? px * (imgW - 1f) / (texW - 1f) : 0f;
                float fitY = texH > 1 ? py * (imgH - 1f) / (texH - 1f) : 0f;
                landmarks.imagePoints.Add(new Vector2(fitX, imgH - 1f - fitY));
            }
            else
            {
                if (previewCam == null || root == null) return;
                LandmarkSet.Anchor a;
                int screenY = texH - 1 - py;
                if (LandmarkCorrespondence.TryAnchorAtPixel(previewCam, root, new Vector2(px, screenY), texW, texH, out a))
                {
                    a.renderPixel = new Vector2(px, screenY);
                    landmarks.renderAnchors.Add(a);
                }
                else
                {
                    EditorUtility.DisplayDialog(Loc.T("fitWindowTitle"), Loc.T("fitAnchorFailed"), "OK");
                    evt.Use();
                    return;
                }
            }
            evt.Use();
            Repaint();
        }

        private void DrawLandmarksOnPane(Rect r, int texW, int texH, bool isRender)
        {
            if (photoOnly && !isRender)
            {
                DrawPhotoAutoCandidates(r, texW, texH);
                foreach (var mark in photoAnnotation.points)
                {
                    if (mark.state != "marked") continue;
                    float gx = r.xMin + (mark.x * (texW - 1) + .5f) / texW * r.width;
                    float gy = r.yMin + (mark.y * (texH - 1) + .5f) / texH * r.height;
                    int index = Array.FindIndex(photoTemplate.points, p => p.id == mark.id);
                    EditorGUI.DrawRect(new Rect(gx - 3, gy - 3, 6, 6), Color.cyan);
                    if(index>=0)GUI.Label(new Rect(gx-5,gy-5,10,10),new GUIContent("",photoTemplate.points[index].label));
                    if (photoEditMode == 2 && index == activePhotoPoint) GUI.Label(new Rect(gx + 6, gy - 18, 240, 20), PhotoDetailLandmarks.ShortLabel(photoTemplate.points[index].label),EditorStyles.whiteMiniLabel);
                }
                if (showProjectionOverlay && lastResult != null && lastResult.fittedLandmarks != null && lastResult.landmarkNames != null)
                {
                    if(lastResult.contourModelSamples!=null&&lastResult.contourImageSamples!=null)
                    {
                        Handles.BeginGUI();var oldColor=Handles.color;Handles.color=Color.yellow;
                        for(int j=0;j<Math.Min(lastResult.contourModelSamples.Length,lastResult.contourImageSamples.Length);j++)
                        {
                            var source=LandmarkToGuiPoint(r,texW,texH,lastResult.contourModelSamples[j],false);var target=LandmarkToGuiPoint(r,texW,texH,lastResult.contourImageSamples[j],false);
                            EditorGUI.DrawRect(new Rect(source.x-2,source.y-2,4,4),Color.yellow);Handles.DrawLine(source,target);
                        }
                        Handles.color=oldColor;Handles.EndGUI();return;
                    }
                    for (int i = 0; i < Math.Min(lastResult.fittedLandmarks.Length, lastResult.landmarkNames.Length); i++)
                    {
                        var observed = photoAnnotation.points.Find(m => m.id == lastResult.landmarkNames[i] && m.state == "marked");
                        if (observed == null) continue;
                        Vector2 p = lastResult.fittedLandmarks[i];
                        Vector2 gui = LandmarkToGuiPoint(r, texW, texH, p, false);
                        EditorGUI.DrawRect(new Rect(gui.x - 3, gui.y - 3, 6, 6), Color.yellow);
                        GUI.Label(new Rect(gui.x-5,gui.y-5,10,10),new GUIContent("","模型拟合投影（不是标注点）；黄线表示与青色标注的偏差"));
                        if (observed != null)
                        {
                            Vector2 target = new Vector2(r.xMin + (observed.x*(texW-1)+.5f)/texW*r.width,
                                r.yMin + (observed.y*(texH-1)+.5f)/texH*r.height);
                            Handles.BeginGUI(); var color = Handles.color; Handles.color = Color.yellow;
                            Handles.DrawLine(gui, target); Handles.color = color; Handles.EndGUI();
                        }
                    }
                }
                return;
            }
            if (photoOnly && isRender) UpdateTemplatePreviewPoints();
            int count = isRender ? landmarks.renderAnchors.Count : landmarks.imagePoints.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 p = isRender ? landmarks.renderAnchors[i].renderPixel : landmarks.imagePoints[i];
                Vector2 gui = LandmarkToGuiPoint(r, texW, texH, p, isRender);
                float gx = gui.x;
                float gy = gui.y;
                EditorGUI.DrawRect(new Rect(gx - 4f, gy - 4f, 8f, 8f), Color.red);
                GUI.Label(new Rect(gx + 5f, gy - 9f, 40f, 16f), (photoOnly ? (Array.FindIndex(photoTemplate.points, p0 => p0.id == landmarks.renderAnchors[i].semanticId) + 1) : i).ToString(), EditorStyles.miniLabel);
            }
        }

        private Vector2 LandmarkToGuiPoint(Rect r, int texW, int texH, Vector2 p, bool isRender)
        {
            float displayX = p.x;
            // Both stored point types are y-up; GUI coordinates are y-down.
            float displayY = texH - 1f - p.y;
            if (!isRender)
            {
                displayX = imgW > 1 ? p.x * (texW - 1f) / (imgW - 1f) : 0f;
                float fitY = imgH - 1f - p.y;
                displayY = imgH > 1 ? fitY * (texH - 1f) / (imgH - 1f) : 0f;
            }
            return new Vector2(r.xMin + (displayX + 0.5f) / texW * r.width,
                r.yMin + (displayY + 0.5f) / texH * r.height);
        }
    }
}
