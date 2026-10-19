using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable]
    public class FitSettings
    {
        public bool recordUndo = true;
        public bool fitImageRoll = true;
        public bool fitEyelids = true;
        public bool fitDenseRegions = true;
        public int starts = 5;
        public bool photoOnlyTemplate;
        public bool stabilizePhotoFit;
        public PhotoAnnotation photoAnnotation;
        public float photoNoisePixels=8f,photoPriorSigma=25f,photoPriorStrength=.25f,photoPixelScale=1;
        public string photoObjectiveVersion=PhotoContourObjective.Version;
        public string photoPriorCenter="weights_at_fit_start";
        [NonSerialized] public PhotoContourObjective contourObjective;
        [NonSerialized] public Dictionary<string,float> priorCenters;
        // Null preserves the original experiment API. The photo window supplies
        // the selected model's reviewed coordinates and actual key names.
        public CommonCoordinateProfile photoProfile;
        public PhotoLandmarkTemplate photoTemplate;
        public string[] photoKeys;
        public List<ShapeCandidate> photoCandidateAudit;
        public int passes = 3;
        public int maxRenders = 5000;
        public float[] probes = { -50f, 0f, 50f, 100f };
        public bool useLandmarks = true;
        public float landmarkWeight = 2.5f;
        public float sparsity = 0.02f;
        public int seed = 20240517;
        public int workWidth = 256;
        public float bgTolerance = 0.12f;
        // Robust landmark loss and candidate diagnostics. Candidates are ranked
        // by projected influence; keeping all shapes by default preserves the
        // existing optimizer behavior until the user explicitly filters them.
        public float landmarkHuberDeltaPixels = 6f;
        public float candidateProbeWeight = 25f;
        public bool rankCandidates = true;
    }

    [Serializable]
    public class ShapeCandidate
    {
        public string rendererPath = "";
        public string name = "";
        public float influencePixels;
        public int changedPixels;
        public bool selected = true;
        public string reason = "";
    }

    [Serializable]
    public class FitWeightChange
    {
        public string name, rendererPath;
        public float before, after;
        public bool atBoundary;
    }

    public class FitResult
    {
        public List<float> startLosses = new List<float>();
        public List<float> startRmses = new List<float>();
        public List<float> startDataLosses=new List<float>(),startPriorLosses=new List<float>(),startBalancedRmses=new List<float>();
        public List<float[]> startInitialWeights = new List<float[]>();
        public List<float[]> startFinalWeights = new List<float[]>();
        public int selectedStart;
        public float initialLandmarkRmse = float.NaN, finalLandmarkRmse = float.NaN;
        public float imageRollDegrees;
        public Vector2[] fittedLandmarks;
        public string[] landmarkNames;
        public float[] initialWeights;
        public List<FitWeightChange> weightChanges = new List<FitWeightChange>();
        public ModelCardData card;
        public float loss;
        public string objectiveVersion;
        public float dataLoss,priorLoss,balancedRmsePixels,initialBalancedRmsePixels;
        public Vector2[] contourModelSamples,contourImageSamples;
        public float[] contourSampleWeights;
        public int renders;
        public bool cancelled;
        public List<ShapeCandidate> candidates = new List<ShapeCandidate>();
    }

    /// <summary>Maps avatar-render pixels into reference-image pixels (foreground bbox alignment).</summary>
    public struct FitNormalizer
    {
        public float scale;
        public float rotationDegrees;
        public Vector2 offset;

        public Vector2 Apply(Vector2 p) { return Rotate(p, rotationDegrees) * scale + offset; }
        public Vector2 ApplyInverse(Vector2 p) { return Rotate((p - offset) / scale, -rotationDegrees); }
        public static Vector2 Rotate(Vector2 p, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(c*p.x-s*p.y,s*p.x+c*p.y);
        }
    }

    /// <summary>
    /// Render-driven fitting: search blend shape weights so the avatar's rendered
    /// silhouette (+ optional landmark pairs) matches a reference image.
    /// Gradient-free: Gauss-Seidel passes over every blend shape with probe values,
    /// using Unity's own renderer as the forward model.
    /// </summary>
    public static class FittingOptimizer
    {
        private class PhotoCameraFrame
        {
            public bool initialized;
            public Vector3 position;
            public Quaternion rotation;
            public float size;
        }
        // Extra freedom requires directly observed contours. Old 12-point
        // annotations do not silently enable mouth opening or brow guesses.
        public static bool DenseCandidate(string name, LandmarkSet lm)
        {
            if (lm == null) return false;
            Func<string, int> count = token => lm.renderAnchors.FindAll(a => a.semanticId != null && a.semanticId.Contains(token)).Count;
            bool eyes = count("_eye_upper_") + count("_eye_lower_") >= 4;
            bool brows = count("left_brow_") >= 3 && count("right_brow_") >= 3;
            bool mouth = count("mouth_upper_") >= 2 && count("mouth_lower_") >= 2;
            bool face = count("left_face_") >= 2 && count("right_face_") >= 2;
            return (eyes && Array.IndexOf(new[]{"eyelid_upper_in_up", "eyelid_upper_out_up", "eyelid_upper_middle_down", "eyelid_lower_middle_up", "eyelid_lower_in_down", "eyelid_lower_out_down", "Eye_rotation_tare", "Eye_rotation_turi"}, name) >= 0)
                || (brows && Array.IndexOf(new[]{"brow_up", "brow_down", "brow_in", "brow_out", "brow_komaru", "brow_angry"}, name) >= 0)
                || (mouth && Array.IndexOf(new[]{"m_a", "m_wide", "m_narrow", "m_smile", "mouth_up", "mouth_down"}, name) >= 0)
                || (face && Array.IndexOf(new[]{"Cheek_width", "Cheek_width-", "Cheek2_width", "Cheek2_width-", "jaw_short", "jaw_long"}, name) >= 0);
        }

        private class ShapeEntry
        {
            public SkinnedMeshRenderer smr;
            public int index;
            public string name;
        }

        public static FitResult Fit(GameObject root, Texture2D image, LandmarkSet landmarks, FitSettings s,
            Action<string, float> progress, Func<bool> cancelled)
        {
            s.contourObjective=null;
            if(s.stabilizePhotoFit&&s.photoOnlyTemplate)
            {
                if(s.photoAnnotation==null||s.photoPriorSigma<=0||s.photoNoisePixels<=0||s.photoPriorStrength<0)throw new InvalidOperationException("缺少图片标注或约束尺度无效。");
                var size=SilhouetteMask.Extract(image,s.workWidth,s.bgTolerance);s.photoPixelScale=(float)image.width/size.width;
                s.priorCenters=new Dictionary<string,float>();
                foreach(var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))if(renderer.sharedMesh!=null)
                    for(int k=0;k<renderer.sharedMesh.blendShapeCount;k++)s.priorCenters[ModelCardIO.GetRelativePath(root.transform,renderer.transform)+"/"+renderer.sharedMesh.GetBlendShapeName(k)]=renderer.GetBlendShapeWeight(k);
            }
            if (s.photoOnlyTemplate && s.photoProfile != null)
                GenericPhotoModel.Prepare(root, image, landmarks, s);
            if (!s.photoOnlyTemplate || s.starts <= 1) return FitSingle(root,image,landmarks,s,progress,cancelled);
            int starts = Mathf.Clamp(s.starts, 1, 5);
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var snapshots = new Dictionary<SkinnedMeshRenderer,float[]>();
            foreach (var r in renderers)
            {
                if (r.sharedMesh == null) continue;
                var values = new float[r.sharedMesh.blendShapeCount];
                for (int i=0;i<values.Length;i++) values[i]=r.GetBlendShapeWeight(i);
                snapshots[r]=values;
            }
            Action restore = () => { foreach(var kv in snapshots) for(int i=0;i<kv.Value.Length;i++) kv.Key.SetBlendShapeWeight(i,kv.Value[i]); };
            var losses=new List<float>(); var rmses=new List<float>();
            var dataLosses=new List<float>();var priorLosses=new List<float>();var balancedRmses=new List<float>();
            var initials=new List<float[]>(); var finals=new List<float[]>();
            FitResult best=null,first=null;int evaluations=0,selected=0;
            var cameraFrame=new PhotoCameraFrame();
            try
            {
                var random=new System.Random(s.seed);
                for(int start=0;start<starts;start++)
                {
                    restore();
                    if(start>0) foreach(var change in first.weightChanges)
                    {
                        foreach(var r in renderers)
                        {
                            if(r.sharedMesh==null || ModelCardIO.GetRelativePath(root.transform,r.transform)!=change.rendererPath) continue;
                            r.SetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex(change.name),(float)random.NextDouble()*100f);
                        }
                    }
                    int active=start;
                    var result=FitSingle(root,image,landmarks,s,progress==null?null:(Action<string,float>)((msg,f)=>progress("初值 "+(active+1)+"/"+starts+": "+msg,(active+f)/starts)),cancelled,cameraFrame);
                    if(first==null)first=result;
                    losses.Add(result.loss);rmses.Add(result.finalLandmarkRmse);evaluations+=result.renders;
                    dataLosses.Add(result.dataLoss);priorLosses.Add(result.priorLoss);balancedRmses.Add(result.balancedRmsePixels);
                    initials.Add((float[])result.initialWeights.Clone());
                    finals.Add(result.weightChanges.ConvertAll(c=>c.after).ToArray());
                    if(best==null||result.loss<best.loss){best=result;selected=start;}
                    if(result.cancelled){best.cancelled=true;break;}
                }
                restore();
                foreach(var change in best.weightChanges)foreach(var r in renderers)
                {
                    if(r.sharedMesh==null||ModelCardIO.GetRelativePath(root.transform,r.transform)!=change.rendererPath)continue;
                    int index=r.sharedMesh.GetBlendShapeIndex(change.name);
                    change.before=snapshots[r][index];r.SetBlendShapeWeight(index,change.after);
                }
                best.initialLandmarkRmse=first.initialLandmarkRmse;best.initialWeights=first.initialWeights;best.initialBalancedRmsePixels=first.initialBalancedRmsePixels;
                best.startLosses=losses;best.startRmses=rmses;best.selectedStart=selected;best.renders=evaluations;
                best.startInitialWeights=initials;best.startFinalWeights=finals;
                best.startDataLosses=dataLosses;best.startPriorLosses=priorLosses;best.startBalancedRmses=balancedRmses;
                return best;
            }
            catch { restore(); throw; }
        }

        private static FitResult FitSingle(GameObject root, Texture2D image, LandmarkSet landmarks, FitSettings s,
            Action<string, float> progress, Func<bool> cancelled, PhotoCameraFrame cameraFrame=null)
        {
            var result = new FitResult();

            var imgMask = SilhouetteMask.Extract(image, s.workWidth, s.bgTolerance);
            if (!imgMask.hasForeground && !s.photoOnlyTemplate) throw new InvalidOperationException("NoForeground");
            int w = imgMask.width, h = imgMask.height;
            s.contourObjective=s.stabilizePhotoFit&&s.photoOnlyTemplate?new PhotoContourObjective(landmarks,s,w,h):null;

            var shapes = new List<ShapeEntry>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s.photoOnlyTemplate && (landmarks == null || !landmarks.renderAnchors.Exists(a => a.smr == smr))) continue;
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    bool basic = Array.IndexOf(new[] { "eye_up", "eye_in", "eye_big", "nose_up" }, name) >= 0;
                    bool lid = s.fitEyelids && Array.IndexOf(new[] { "eyelid_upper_middle_up", "eyelid_lower_center_down", "Eye_tare0" }, name) >= 0;
                    bool dense = s.fitDenseRegions && DenseCandidate(name, landmarks);
                    if (!s.fitEyelids && (name.StartsWith("eyelid_") || name.StartsWith("Eye_"))) dense = false;
                    if (s.photoOnlyTemplate && (s.photoProfile != null
                        ? s.photoKeys == null || Array.IndexOf(s.photoKeys, name) < 0
                        : !basic && !lid && !dense)) continue;
                    shapes.Add(new ShapeEntry { smr = smr, index = i, name = mesh.GetBlendShapeName(i) });
                }
            }
            if (shapes.Count == 0) throw new InvalidOperationException("NoShapes");

            var weights = new float[shapes.Count];
            for (int i = 0; i < shapes.Count; i++)
            {
                weights[i] = shapes[i].smr.GetBlendShapeWeight(shapes[i].index);
                result.weightChanges.Add(new FitWeightChange { name = shapes[i].name, before = weights[i], rendererPath = ModelCardIO.GetRelativePath(root.transform, shapes[i].smr.transform) });
            }

            result.initialWeights = (float[])weights.Clone();
            Texture2D renderTex;
            Camera cam;
            if (!AvatarFitRenderer.RenderAvatar(root, w, h, out renderTex, out cam))
                throw new InvalidOperationException("RenderFailed");

            try
            {
                if (s.photoOnlyTemplate)
                {
                    if (s.photoProfile != null)
                        PhotoLandmarkTemplate.ConfigureCamera(s.photoTemplate, landmarks.renderAnchors[0].smr, cam, w, h);
                    else PhotoLandmarkTemplate.ConfigureCamera(root, cam, w, h);
                    // All starts use the original source camera frame. Building
                    // it from randomized shapes would change the forward model.
                    if(cameraFrame!=null)
                    {
                        if(!cameraFrame.initialized)
                        {
                            cameraFrame.position=cam.transform.position;cameraFrame.rotation=cam.transform.rotation;
                            cameraFrame.size=cam.orthographicSize;cameraFrame.initialized=true;
                        }
                        else
                        {
                            cam.transform.SetPositionAndRotation(cameraFrame.position,cameraFrame.rotation);
                            cam.orthographicSize=cameraFrame.size;
                        }
                    }
                    renderTex = AvatarFitRenderer.RenderWithAvatar(root, cam, w, h, renderTex);
                }
                var normalizer = FitNormalizerFromMasks(imgMask, AvatarFitRenderer.GetMask(renderTex), w, h);
                if (s.photoOnlyTemplate) normalizer = FitNormalizerFromLandmarks(landmarks, cam, w, h, s.fitImageRoll);
                if(s.contourObjective!=null){s.contourObjective.Evaluate(cam,w,h);normalizer=s.contourObjective.Normalizer;result.initialBalancedRmsePixels=s.contourObjective.RmsePixels;}
                result.initialLandmarkRmse = LandmarkRmse(landmarks, cam, w, h, normalizer);
                float[] imgMaskArr = ToFloat(imgMask.mask);
                if (s.photoProfile != null) result.candidates = new List<ShapeCandidate>(s.photoCandidateAudit);
                else if (s.rankCandidates)
                {
                    result.candidates = RankCandidates(root, shapes, weights, cam, renderTex, w, h, normalizer, landmarks, s);
                }
                if (s.recordUndo) Undo.RegisterFullObjectHierarchyUndo(root, "Fit Avatar from Image");

                float bestLoss = float.MaxValue;
                var rng = new System.Random(s.seed);
                var order = new int[shapes.Count];
                for (int i = 0; i < order.Length; i++) order[i] = i;

                int totalPasses = s.photoOnlyTemplate ? Mathf.Max(9, s.passes) : s.passes;
                for (int pass = 0; pass < totalPasses && result.renders < s.maxRenders && !result.cancelled; pass++)
                {
                    Shuffle(order, rng);
                    for (int oi = 0; oi < order.Length && result.renders < s.maxRenders && !result.cancelled; oi++)
                    {
                        int si = order[oi];
                        ApplyWeights(shapes, weights);
                        float cur = Eval(root, shapes, weights, imgMaskArr, normalizer, cam, renderTex, w, h, landmarks, s);
                        result.renders++;
                        float best = cur;
                        float bestV = weights[si];
                        float[] probes = s.probes;
                        if (s.photoOnlyTemplate && pass >= 3)
                        {
                            float step = pass < 6 ? 5f : 1f;
                            probes = new float[] { Mathf.Clamp(bestV-step,0,100), Mathf.Clamp(bestV+step,0,100) };
                        }
                        for (int p = 0; p < probes.Length && result.renders < s.maxRenders; p++)
                        {
                            weights[si] = probes[p];
                            ApplyWeights(shapes, weights);
                            float loss = Eval(root, shapes, weights, imgMaskArr, normalizer, cam, renderTex, w, h, landmarks, s);
                            result.renders++;
                            if (loss < best) { best = loss; bestV = probes[p]; }
                        }
                        weights[si] = bestV;
                        if (best < bestLoss) bestLoss = best;
                        if (progress != null && (oi % 5 == 0))
                        {
                            progress(string.Format("{0} (pass {1}/{2})", shapes[si].name, pass + 1, totalPasses),
                                (pass * shapes.Count + oi) / (float)(totalPasses * shapes.Count));
                        }
                        if (cancelled != null && cancelled()) result.cancelled = true;
                    }
                }

                ApplyWeights(shapes, weights);
                if (s.photoOnlyTemplate) normalizer = FitNormalizerFromLandmarks(landmarks, cam, w, h, s.fitImageRoll);
                float finalLoss=Eval(root,shapes,weights,imgMaskArr,normalizer,cam,renderTex,w,h,landmarks,s);
                result.renders++;
                if(s.contourObjective!=null)
                {
                    normalizer=s.contourObjective.Normalizer;result.objectiveVersion=PhotoContourObjective.Version;
                    result.dataLoss=s.contourObjective.DataLoss;result.priorLoss=PhotoPriorLoss(root,shapes,weights,s);
                    result.balancedRmsePixels=s.contourObjective.RmsePixels;
                    result.contourModelSamples=s.contourObjective.ModelSamples;result.contourImageSamples=s.contourObjective.ImageSamples;
                    result.contourSampleWeights=s.contourObjective.SampleWeights;
                }
                result.imageRollDegrees = normalizer.rotationDegrees;
                result.fittedLandmarks = landmarks != null ? ProjectAnchors(landmarks, cam, w, h, normalizer) : null;
                result.landmarkNames = landmarks != null ? landmarks.renderAnchors.ConvertAll(a => a.semanticId).ToArray() : null;
                result.finalLandmarkRmse = LandmarkRmse(landmarks, cam, w, h, normalizer);
                for (int i = 0; i < weights.Length; i++)
                {
                    result.weightChanges[i].after = weights[i];
                    result.weightChanges[i].atBoundary = s.photoOnlyTemplate && (weights[i] <= .01f || weights[i] >= 99.99f);
                }
                result.loss = finalLoss;
                result.card = BuildCard(root, shapes, weights);
                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(renderTex);
                if (cam != null) UnityEngine.Object.DestroyImmediate(cam.gameObject);
            }
        }

        // ------------------------------------------------------------------ eval
        public static float LandmarkRmse(LandmarkSet landmarks, Camera camera, int width, int height, FitNormalizer normalizer)
        {
            if (landmarks == null || landmarks.Count == 0) return float.NaN;
            var points = ProjectAnchors(landmarks, camera, width, height, normalizer);
            double sum = 0;
            for (int i = 0; i < landmarks.Count; i++)
            {
                if (float.IsNaN(points[i].x) || float.IsNaN(points[i].y)) return float.NaN;
                sum += (points[i] - landmarks.imagePoints[i]).sqrMagnitude;
            }
            return (float)Math.Sqrt(sum / (2 * landmarks.Count));
        }

        private static float Eval(GameObject root, List<ShapeEntry> shapes, float[] weights, float[] imgMask, FitNormalizer n,
            Camera cam, Texture2D renderTex, int w, int h, LandmarkSet lm, FitSettings s)
        {
            if(s.contourObjective!=null)return s.contourObjective.Evaluate(cam,w,h)+PhotoPriorLoss(root,shapes,weights,s);
            float loss = 0;
            if (s.photoOnlyTemplate) n = FitNormalizerFromLandmarks(lm, cam, w, h, s.fitImageRoll);
            else
            {
                AvatarFitRenderer.RenderWithAvatar(root, cam, w, h, renderTex);
                float[] ren = ResampleMask(AvatarFitRenderer.GetMask(renderTex), w, h, n);
                loss = 1f - IoU(imgMask, ren);
            }
            if (lm != null && lm.Count > 0 && s.useLandmarks)
            {
                loss += s.landmarkWeight * LandmarkLoss(lm, cam, w, h, n, s);
            }
            int nz = 0;
            for (int i = 0; i < weights.Length; i++) if (Mathf.Abs(weights[i]) > 0.5f) nz++;
            if (!s.photoOnlyTemplate) loss += s.sparsity * nz;
            return loss;
        }

        private static float PhotoPriorLoss(GameObject root,List<ShapeEntry> shapes,float[] weights,FitSettings s)
        {
            double sum=0;
            for(int i=0;i<shapes.Count;i++)
            {
                float center=s.priorCenters[ModelCardIO.GetRelativePath(root.transform,shapes[i].smr.transform)+"/"+shapes[i].name];
                float z=(weights[i]-center)/s.photoPriorSigma;sum+=.5*z*z;
            }
            return (float)(s.photoPriorStrength*sum/Math.Max(1,shapes.Count));
        }

        private static float LandmarkLoss(LandmarkSet lm, Camera cam, int w, int h, FitNormalizer n, FitSettings s)
        {
            float total = 0f;
            int cnt = 0;
            var cache = new Dictionary<SkinnedMeshRenderer, Mesh>();
            try
            {
                for (int i = 0; i < lm.Count; i++)
                {
                    var a = lm.renderAnchors[i];
                    if (a == null || a.smr == null || a.vertexIndex < 0) continue;
                    Mesh baked;
                    if (!cache.TryGetValue(a.smr, out baked))
                    {
                        baked = new Mesh();
                        a.smr.BakeMesh(baked);
                        cache[a.smr] = baked;
                    }
                    if (a.vertexIndex >= baked.vertexCount) continue;
                    Vector3 world = a.useWorldSpace ? a.Position(baked.vertices)
                        : a.smr.transform.TransformPoint(a.Position(baked.vertices));
                    Vector3 sp = WorldToRenderPixel(cam, world, w, h);
                    Vector2 np = n.Apply(new Vector2(sp.x, sp.y));
                    float distance = Vector2.Distance(np, lm.imagePoints[i]);
                    // Huber loss limits one badly placed/occluded point to a
                    // linear contribution instead of allowing it to dominate.
                    float delta = Mathf.Max(safeHuberDelta(s.landmarkHuberDeltaPixels), 1e-4f);
                    float quadratic = Mathf.Min(distance, delta);
                    total += 0.5f * quadratic * quadratic + delta * Mathf.Max(distance - delta, 0f);
                    cnt++;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ModelCard] Landmark evaluation failed: " + e.Message);
            }
            finally
            {
                foreach (var kv in cache) UnityEngine.Object.DestroyImmediate(kv.Value);
            }
            return cnt > 0 ? total / (cnt * (s.photoOnlyTemplate ? 1f : Mathf.Max(h, 1) * Mathf.Max(h, 1))) : 0f;
        }

        private static float safeHuberDelta(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 6f : value;
        }

        private static List<ShapeCandidate> RankCandidates(GameObject root, List<ShapeEntry> shapes, float[] weights,
            Camera cam, Texture2D renderTex, int w, int h, FitNormalizer n, LandmarkSet lm, FitSettings s)
        {
            var ranked = new List<ShapeCandidate>();
            Vector2[] baseline = lm != null && lm.Count > 0 ? ProjectAnchors(lm, cam, w, h, n) : null;
            bool[] baseMask = baseline == null ? AvatarFitRenderer.GetMask(renderTex) : null;
            for (int i = 0; i < shapes.Count; i++)
            {
                float old = weights[i];
                float probe = Mathf.Clamp(old + s.candidateProbeWeight, 0f, 100f);
                if (Mathf.Abs(probe - old) < 0.001f) probe = Mathf.Clamp(old - s.candidateProbeWeight, 0f, 100f);
                weights[i] = probe;
                ApplyWeights(shapes, weights);
                AvatarFitRenderer.RenderWithAvatar(root, cam, w, h, renderTex);
                float influence = 0f;
                int changed = 0;
                if (baseline != null)
                {
                    Vector2[] moved = ProjectAnchors(lm, cam, w, h, n);
                    int count = 0;
                    for (int p = 0; p < baseline.Length; p++)
                    {
                        if (baseline[p].x < 0f || moved[p].x < 0f) continue;
                        influence += Vector2.Distance(baseline[p], moved[p]); count++;
                    }
                    influence = count > 0 ? influence / count : 0f;
                }
                else
                {
                    bool[] mask = AvatarFitRenderer.GetMask(renderTex);
                    int total = Mathf.Min(baseMask.Length, mask.Length);
                    for (int p = 0; p < total; p++) if (baseMask[p] != mask[p]) changed++;
                    influence = changed / (float)Mathf.Max(total, 1) * Mathf.Max(w, h);
                }
                weights[i] = old;
                ranked.Add(new ShapeCandidate {
                    rendererPath = ModelCardIO.GetRelativePath(shapes[i].smr.transform.root, shapes[i].smr.transform),
                    name = shapes[i].name,
                    influencePixels = influence,
                    changedPixels = changed,
                    selected = influence >= 0.25f,
                    reason = influence >= 0.25f ? "projected influence above 0.25 px" : "projected influence below 0.25 px"
                });
            }
            ApplyWeights(shapes, weights);
            ranked.Sort((a, b) => b.influencePixels.CompareTo(a.influencePixels));
            return ranked;
        }

        public static Vector2[] ProjectAnchors(LandmarkSet lm, Camera cam, int w, int h, FitNormalizer n)
        {
            var points = new Vector2[lm.Count];
            for (int i = 0; i < points.Length; i++) points[i] = new Vector2(-1f, -1f);
            var cache = new Dictionary<SkinnedMeshRenderer, Mesh>();
            try
            {
                for (int i = 0; i < lm.Count; i++)
                {
                    var a = lm.renderAnchors[i];
                    if (a == null || a.smr == null || a.vertexIndex < 0) continue;
                    Mesh baked;
                    if (!cache.TryGetValue(a.smr, out baked)) { baked = new Mesh(); a.smr.BakeMesh(baked); cache[a.smr] = baked; }
                    if (a.vertexIndex >= baked.vertexCount) continue;
                    Vector3 world = a.useWorldSpace ? a.Position(baked.vertices) : a.smr.transform.TransformPoint(a.Position(baked.vertices));
                    Vector3 sp = WorldToRenderPixel(cam, world, w, h);
                    points[i] = n.Apply(new Vector2(sp.x, sp.y));
                }
            }
            finally { foreach (var kv in cache) UnityEngine.Object.DestroyImmediate(kv.Value); }
            return points;
        }

        private static Vector3 WorldToRenderPixel(Camera cam, Vector3 world, int width, int height)
        {
            Vector3 viewport = cam.WorldToViewportPoint(world);
            return new Vector3(viewport.x * width, viewport.y * height, viewport.z);
        }

        public static FitNormalizer FitNormalizerFromLandmarks(LandmarkSet lm, Camera cam, int w, int h, bool fitRoll = false)
        {
            if (lm == null || lm.Count < 4) throw new InvalidOperationException("至少标记 4 个分布在眼部和下半脸的可见点。");
            var neutral = new FitNormalizer { scale = 1f, offset = Vector2.zero };
            var projected = ProjectAnchors(lm, cam, w, h, neutral);
            Vector2 source = Vector2.zero, target = Vector2.zero;
            for (int i = 0; i < lm.Count; i++) { source += projected[i]; target += lm.imagePoints[i]; }
            source /= lm.Count; target /= lm.Count;
            float numerator = 0f, cross = 0f, denominator = 0f;
            for (int i = 0; i < lm.Count; i++)
            {
                Vector2 a = projected[i] - source, b = lm.imagePoints[i] - target;
                numerator += Vector2.Dot(a, b);
                cross += a.x*b.y-a.y*b.x;
                denominator += (projected[i] - source).sqrMagnitude;
            }
            if (denominator < .0001f || numerator <= 0) throw new InvalidOperationException("特征点分布无效，请检查左右顺序与可见点。");
            float angle = fitRoll ? Mathf.Clamp(Mathf.Atan2(cross, numerator) * Mathf.Rad2Deg, -25f, 25f) : 0;
            float rad = angle * Mathf.Deg2Rad;
            float scale = (numerator*Mathf.Cos(rad)+cross*Mathf.Sin(rad)) / denominator;
            return new FitNormalizer { scale = scale, rotationDegrees = angle, offset = target - FitNormalizer.Rotate(source, angle) * scale };
        }

        // ----------------------------------------------------------------- tools
        private static ModelCardData BuildCard(GameObject root, List<ShapeEntry> shapes, float[] weights)
        {
            var card = new ModelCardData
            {
                avatarName = root.name,
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            var byMesh = new Dictionary<SkinnedMeshRenderer, MeshAdjustment>();
            for (int i = 0; i < shapes.Count; i++)
            {
                if (Mathf.Abs(weights[i]) < 1f) continue;
                MeshAdjustment adj;
                if (!byMesh.TryGetValue(shapes[i].smr, out adj))
                {
                    adj = new MeshAdjustment { path = ModelCardIO.GetRelativePath(root.transform, shapes[i].smr.transform) };
                    byMesh[shapes[i].smr] = adj;
                    card.meshes.Add(adj);
                }
                adj.shapes.Add(new ShapeAdjustment { name = shapes[i].name, value = weights[i] });
            }
            return card;
        }

        private static void ApplyWeights(List<ShapeEntry> shapes, float[] weights)
        {
            for (int i = 0; i < shapes.Count; i++)
            {
                shapes[i].smr.SetBlendShapeWeight(shapes[i].index, weights[i]);
            }
        }

        private static void Shuffle(int[] order, System.Random rng)
        {
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = order[i]; order[i] = order[j]; order[j] = t;
            }
        }

        private static FitNormalizer FitNormalizerFromMasks(SilhouetteMask.Result img, bool[] renMask, int w, int h)
        {
            var n = new FitNormalizer { scale = 1f, offset = Vector2.zero };
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!renMask[y * w + x]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < minX || !img.hasForeground) return n;
            float imgCx = (img.minX + img.maxX) * 0.5f;
            float imgCy = (img.minY + img.maxY) * 0.5f;
            float renCx = (minX + maxX) * 0.5f;
            float renCy = (minY + maxY) * 0.5f;
            float imgH = img.maxY - img.minY + 1f;
            float renH = maxY - minY + 1f;
            n.scale = imgH / Mathf.Max(renH, 1f);
            n.offset = new Vector2(imgCx - renCx * n.scale, imgCy - renCy * n.scale);
            return n;
        }

        private static float[] ResampleMask(bool[] src, int w, int h, FitNormalizer n)
        {
            var dst = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector2 sp = n.ApplyInverse(new Vector2(x, y));
                    int sx = Mathf.RoundToInt(sp.x);
                    int sy = Mathf.RoundToInt(sp.y);
                    dst[y * w + x] = (sx >= 0 && sx < w && sy >= 0 && sy < h && src[sy * w + sx]) ? 1f : 0f;
                }
            }
            return dst;
        }

        private static float[] ToFloat(bool[] mask)
        {
            var f = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++) f[i] = mask[i] ? 1f : 0f;
            return f;
        }

        private static float IoU(float[] a, float[] b)
        {
            float inter = 0f, union = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                bool fa = a[i] > 0.5f, fb = b[i] > 0.5f;
                if (fa || fb) union += 1f;
                if (fa && fb) inter += 1f;
            }
            return union > 0f ? inter / union : 0f;
        }
    }
}
