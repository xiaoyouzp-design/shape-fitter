using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public partial class ModelCardFitWindow
    {
        [Serializable] private class AutoPhotoResponse : PhotoAnnotation
        {
            public string status, message;
            public string engineLabel;
            public int faceCount;
        }
        private System.Diagnostics.Process autoProcess;
        private AutoPhotoResponse autoCandidates;
        private string autoOutput, autoInputHash, autoStatus = "";
        private double autoStarted;
        private bool autoSettings;
        private static string AutoWorkspace { get { return Path.GetFullPath(Path.Combine(Application.dataPath,"../Library/形态建拟合器自动标点")); } }
        private static string AutoScriptRoot
        {
            get
            {
                string script=AssetDatabase.FindAssets("PhotoAutoLandmarks t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(p=>p.EndsWith("/PhotoAutoLandmarks.cs",StringComparison.Ordinal));
                string root=string.IsNullOrEmpty(script)?"Assets/形态建拟合器":Path.GetDirectoryName(Path.GetDirectoryName(script));
                return Path.GetFullPath(Path.Combine(root,"Tools/AutoLandmarks"));
            }
        }

        private static string AutoSetting(string name, string fallback)
        { return EditorPrefs.GetString("ModelCard.PhotoAuto." + name, fallback); }

        private static bool UseAnimeDetector { get { return EditorPrefs.GetInt("ModelCard.PhotoAuto.Engine",0)==0; } }
        private static string AutoPathKey(string key) { return UseAnimeDetector ? "Anime"+key : key; }
        private static string AutoPythonDefault { get { return AutoWorkspace+(UseAnimeDetector?"/.anime_auto_venv/Scripts/python.exe":"/.photo_auto_venv/Scripts/python.exe"); } }
        private static string AutoScriptDefault { get { return AutoScriptRoot+(UseAnimeDetector?"/anime_photo_landmarks.py":"/photo_auto_landmarks.py"); } }
        private static string AutoModelDefault { get { return AutoWorkspace+(UseAnimeDetector?"/photo_auto_models/anime/engine.json":"/photo_auto_models/face_landmarker.task"); } }

        private void DrawPhotoAutoControls()
        {
            int selected=UseAnimeDetector?0:1;
            int next=EditorGUILayout.Popup("标点引擎",selected,new[]{"二次元（本地 · 28 点）","真人（本地 · MediaPipe）"});
            if(next!=selected)
            {
                CancelPhotoAuto();autoCandidates=null;autoStatus="引擎已切换，已有标注保留。";
                EditorPrefs.SetInt("ModelCard.PhotoAuto.Engine",next);
            }
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(imageTex == null || photoTemplate == null || autoProcess != null))
                if (GUILayout.Button(UseAnimeDetector?"自动标点（二次元 · 本地）":"自动标点（真人 · 本地）", GUILayout.Height(28))) StartPhotoAuto();
            if (autoProcess != null && GUILayout.Button("取消检测", GUILayout.Height(28))) CancelPhotoAuto();
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(autoStatus)) EditorGUILayout.HelpBox(autoStatus, MessageType.Info);
            if (autoCandidates != null)
            {
                EditorGUILayout.LabelField("橙色为待确认候选，青色为已使用的点；确认后可点击修正或跳过。", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("确认候选，补充未标点")) AcceptPhotoAuto();
                if (GUILayout.Button("丢弃候选")) { autoCandidates = null; autoStatus = "已丢弃自动候选，原标注保留。"; }
                EditorGUILayout.EndHorizontal();
            }
            autoSettings = EditorGUILayout.Foldout(autoSettings, "自动标点设置", true);
            if (autoSettings)
            {
                AutoPathField("Python 路径", AutoPathKey("Python"), AutoPythonDefault);
                AutoPathField("检测脚本", AutoPathKey("Script"), AutoScriptDefault);
                AutoPathField(UseAnimeDetector?"引擎模型配置":"检测模型", AutoPathKey("Model"), AutoModelDefault);
                EditorGUILayout.LabelField(UseAnimeDetector
                    ?"使用本地 YOLOv3 + HRNetV2（hysts），面向近正面二次元脸。最多提供 28 个候选，不插值补造其余点；低热图响应点排除，遮挡点仍需检查。安装说明：Assets/形态建拟合器/Documentation/自动标点安装.zh-CN.md。"
                    :"使用本地 MediaPipe Face Landmarker，适合真人。多张脸请先裁剪，检测点仍需检查。", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void AutoPathField(string label, string key, string fallback)
        {
            string old = AutoSetting(key, fallback);
            string value = EditorGUILayout.TextField(label, old);
            if (value != old) EditorPrefs.SetString("ModelCard.PhotoAuto." + key, value);
        }

        private static string QuoteAutoArg(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                throw new InvalidOperationException("检测路径含无效字符。");
            return "\"" + value + "\"";
        }

        private void StartPhotoAuto()
        {
            CancelPhotoAuto(); autoCandidates = null;
            try
            {
                if (imageTex == null || imageBytes == null || photoTemplate == null)
                    throw new InvalidOperationException("先载入图片及模型端模板。");
                string python = AutoSetting(AutoPathKey("Python"), AutoPythonDefault);
                string script = AutoSetting(AutoPathKey("Script"), AutoScriptDefault);
                string model = AutoSetting(AutoPathKey("Model"), AutoModelDefault);
                string mappingName=UseAnimeDetector?"AnimePhotoMapping":"PhotoAutoMapping";
                string mapping = AssetDatabase.FindAssets(mappingName).Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => p.EndsWith("/"+mappingName+".json", StringComparison.Ordinal));
                foreach (string file in new[] { python, script, model, mapping })
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                        throw new InvalidOperationException("自动标点文件缺失：" + file + "。请检查自动标点设置；二次元安装说明见 Assets/形态建拟合器/Documentation/自动标点安装.zh-CN.md。");
                string dir = Path.GetFullPath(Path.Combine("Library", "PhotoAutoLandmarks", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(dir);
                string input = Path.Combine(dir, "input.png");
                // Detect exactly the image Unity displays, avoiding EXIF decoder differences.
                File.WriteAllBytes(input, imageTex.EncodeToPNG());
                autoOutput = Path.Combine(dir, "candidates.json");
                autoInputHash = PhotoLandmarkTemplate.Hash(imageBytes);
                var info = new System.Diagnostics.ProcessStartInfo {
                    FileName = python, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    Arguments = "-X utf8 " + QuoteAutoArg(script) + " --image " + QuoteAutoArg(input)
                        + " --model " + QuoteAutoArg(model) + " --mapping " + QuoteAutoArg(Path.GetFullPath(mapping))
                        + " --output " + QuoteAutoArg(autoOutput) + " --source-hash " + QuoteAutoArg(autoInputHash)
                };
                autoProcess = new System.Diagnostics.Process { StartInfo = info };
                autoProcess.Start(); autoProcess.BeginOutputReadLine(); autoProcess.BeginErrorReadLine();
                autoStarted = EditorApplication.timeSinceStartup;
                autoStatus = (UseAnimeDetector?"正在本地检测二次元脸":"正在本地检测真人脸")+"，原标注保留；可继续手工标点。";
            }
            catch (Exception e) { CancelPhotoAuto(); autoStatus = e.Message; }
            Repaint();
        }

        private void CancelPhotoAuto()
        {
            if (autoProcess == null) return;
            try { if (!autoProcess.HasExited) autoProcess.Kill(); } catch (InvalidOperationException) { }
            finally { autoProcess.Dispose(); autoProcess = null; }
            autoStatus = "检测已取消，原标注保留。";
        }

        private void PollPhotoAuto()
        {
            if (autoProcess == null) return;
            try
            {
                if (EditorApplication.timeSinceStartup - autoStarted > 120)
                { CancelPhotoAuto(); autoStatus = "检测超时，原标注保留。请检查依赖或手工标点。"; Repaint(); return; }
                if (!autoProcess.HasExited) return;
                int code = autoProcess.ExitCode;
                autoProcess.Dispose(); autoProcess = null;
                if (imageBytes == null || PhotoLandmarkTemplate.Hash(imageBytes) != autoInputHash)
                { autoStatus = "图片已改变，已丢弃旧图片检测结果。"; return; }
                if (!File.Exists(autoOutput)) throw new InvalidOperationException("检测程序未生成结果（退出码 " + code + "），请检查 Python 依赖和脚本路径。");
                var candidate = JsonUtility.FromJson<AutoPhotoResponse>(File.ReadAllText(autoOutput));
                if (candidate == null) throw new InvalidOperationException("检测结果格式无效。");
                autoStatus = candidate.message;
                if (candidate.status != "ok") return;
                if (code != 0 || candidate.faceCount != 1) throw new InvalidOperationException("检测程序返回不一致的结果，未使用候选点。");
                candidate.Validate(photoTemplate, autoInputHash, imageTex.width, imageTex.height);
                autoCandidates = candidate;
                autoStatus += " 尚未用于拟合。遮挡、眉、唇和脸廓点需检查；不支持的点可手工补充。";
            }
            catch (Exception e) { CancelPhotoAuto(); autoCandidates = null; autoStatus = e.Message; }
            finally { if (autoProcess == null) Repaint(); }
        }

        private bool IsPhotoAutoEligible(string id)
        {
            var old = photoAnnotation.points.Find(p => p.id == id);
            return old == null || old.state == "unmarked";
        }

        private void AcceptPhotoAuto()
        {
            if (autoCandidates == null || imageBytes == null || imageTex == null) return;
            try
            {
                autoCandidates.Validate(photoTemplate, PhotoLandmarkTemplate.Hash(imageBytes), imageTex.width, imageTex.height);
                int added = 0;
                foreach (var p in autoCandidates.points)
                {
                    if (!IsPhotoAutoEligible(p.id)) continue;
                    photoAnnotation.Set(p.id, p.x, p.y, "marked"); added++;
                }
                photoAnnotation.annotationNote = "含经用户确认的 "+(string.IsNullOrEmpty(autoCandidates.engineLabel)?"MediaPipe":autoCandidates.engineLabel)
                    +" 自动候选；仍可修正或跳过。检测记录：" + autoOutput;
                autoCandidates = null; RebuildPhotoLandmarks(); ClearFitResult();
                autoStatus = "已补充 " + added + " 个点，原有标注和跳过点保留。检查图片后可开始拟合。";
            }
            catch (Exception e) { autoCandidates = null; autoStatus = e.Message; }
            Repaint();
        }

        private void DrawPhotoAutoCandidates(Rect r, int width, int height)
        {
            if (autoCandidates == null || photoTemplate == null) return;
            foreach (var p in autoCandidates.points)
            {
                if (!IsPhotoAutoEligible(p.id)) continue;
                float x = r.xMin + (p.x * (width - 1) + .5f) / width * r.width;
                float y = r.yMin + (p.y * (height - 1) + .5f) / height * r.height;
                EditorGUI.DrawRect(new Rect(x - 3, y - 3, 6, 6), new Color(1f, .55f, 0));
                GUI.Label(new Rect(x + 4, y - 9, 40, 20), (Array.FindIndex(photoTemplate.points, d => d.id == p.id) + 1).ToString());
            }
        }
    }
}
