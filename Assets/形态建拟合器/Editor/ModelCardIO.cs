using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// High-level IO: capture an avatar's blend shapes + bones into a
    /// <see cref="ModelCardData"/>, apply a card back onto an avatar, and
    /// encode/decode cards into PNG files (chunk + optional pixel backup).
    /// </summary>
    public static class ModelCardIO
    {
        // -------------------------------------------------------------- capture
        /// <summary>
        /// Capture the current state of the avatar hierarchy.
        /// </summary>
        /// <param name="includeAllShapes">If false, only non-zero blend shape weights are stored.</param>
        /// <param name="recordAllBones">If false, only bones whose local TRS differs from identity are stored.</param>
        public static ModelCardData Capture(GameObject root, bool includeAllShapes, bool recordAllBones)
        {
            var card = new ModelCardData();
            card.avatarName = root.name;
            card.createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in renderers)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                var adj = new MeshAdjustment { path = GetRelativePath(root.transform, smr.transform) };
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    float w = smr.GetBlendShapeWeight(i);
                    if (includeAllShapes || w != 0f)
                    {
                        adj.shapes.Add(new ShapeAdjustment { name = mesh.GetBlendShapeName(i), value = w });
                    }
                }
                if (adj.shapes.Count > 0) card.meshes.Add(adj);
            }

            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t == root.transform) continue;
                bool identity = t.localPosition == Vector3.zero
                    && t.localRotation == Quaternion.identity
                    && t.localScale == Vector3.one;
                if (!recordAllBones && identity) continue;
                card.bones.Add(new BoneAdjustment
                {
                    path = GetRelativePath(root.transform, t),
                    localPosition = t.localPosition,
                    localRotation = t.localRotation,
                    localScale = t.localScale,
                });
            }
            return card;
        }

        // ------------------------------------------------------------- baseline
        // In-memory per-session baselines (keyed by instanceID) so a card apply
        // can always be reverted to the pre-apply state. NOTE: never reset bones
        // to identity - real rigs have non-identity local transforms by design.
        private static readonly Dictionary<int, ModelCardData> Baselines = new Dictionary<int, ModelCardData>();

        /// <summary>Capture a full-state baseline (all shapes + all bones) for later restore.</summary>
        public static ModelCardData CaptureBaseline(GameObject root)
        {
            if (root == null) return null;
            var card = Capture(root, true, true);
            Baselines[root.GetInstanceID()] = card;
            return card;
        }

        public static bool HasBaseline(GameObject root)
        {
            return root != null && Baselines.ContainsKey(root.GetInstanceID());
        }

        /// <summary>Restore the captured baseline. Returns the number of adjustments, or -1 when absent.</summary>
        public static int RestoreBaseline(GameObject root)
        {
            ModelCardData baseline;
            if (root == null || !Baselines.TryGetValue(root.GetInstanceID(), out baseline)) return -1;
            return Apply(root, baseline, true, true);
        }

        // ---------------------------------------------------------------- apply
        /// <summary>Apply a card to an avatar. Returns the number of applied adjustments.</summary>
        public static int Apply(GameObject root, ModelCardData card, bool applyShapes, bool applyBones)
        {
            int applied = 0;
            Undo.RegisterFullObjectHierarchyUndo(root, "Apply Model Card");

            if (applyShapes)
            {
                foreach (var m in card.meshes)
                {
                    var smr = FindRenderer(root.transform, m.path);
                    if (smr == null || smr.sharedMesh == null)
                    {
                        Debug.LogWarning("[ModelCard] Renderer not found: " + m.path);
                        continue;
                    }
                    Mesh mesh = smr.sharedMesh;
                    foreach (var s in m.shapes)
                    {
                        int idx = mesh.GetBlendShapeIndex(s.name);
                        if (idx < 0)
                        {
                            Debug.LogWarning("[ModelCard] Blend shape '" + s.name + "' not found on " + m.path);
                            continue;
                        }
                        smr.SetBlendShapeWeight(idx, s.value);
                        applied++;
                    }
                }
            }

            if (applyBones)
            {
                foreach (var b in card.bones)
                {
                    var t = FindTransform(root.transform, b.path);
                    if (t == null)
                    {
                        Debug.LogWarning("[ModelCard] Bone not found: " + b.path);
                        continue;
                    }
                    t.localPosition = b.localPosition;
                    t.localRotation = b.localRotation;
                    t.localScale = b.localScale;
                    applied++;
                }
            }

            if (applied > 0)
            {
                EditorUtility.SetDirty(root);
                SceneView.RepaintAll();
            }
            return applied;
        }

        // --------------------------------------------------------------- payload
        /// <summary>magic (8 bytes) + deflate(JSON) — the shared payload format for both channels.</summary>
        public static byte[] EncodePayload(ModelCardData card)
        {
            string json = JsonUtility.ToJson(card, false);
            byte[] compressed = PngCodec.Compress(Encoding.UTF8.GetBytes(json));
            var payload = new byte[8 + compressed.Length];
            Array.Copy(Encoding.ASCII.GetBytes(ModelCardData.Magic), payload, 8);
            Array.Copy(compressed, 0, payload, 8, compressed.Length);
            return payload;
        }

        public static ModelCardData DecodePayload(byte[] payload)
        {
            if (payload == null || payload.Length < 8) return null;
            byte[] magic = Encoding.ASCII.GetBytes(ModelCardData.Magic);
            for (int i = 0; i < 8; i++)
            {
                if (payload[i] != magic[i]) return null;
            }
            try
            {
                byte[] jsonBytes = PngCodec.Decompress(payload, 8, payload.Length - 8);
                string json = Encoding.UTF8.GetString(jsonBytes);
                return JsonUtility.FromJson<ModelCardData>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ModelCard] Failed to decode payload: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------ PNG file IO
        /// <summary>
        /// Build the final card PNG: (optional) LSB pixel backup first, then the
        /// "mcrd" chunk. The chunk is the primary channel; the pixels are the backup.
        /// </summary>
        public static byte[] SaveCardToPng(ModelCardData card, byte[] baseImage, bool pixelBackup)
        {
            byte[] payload = EncodePayload(card);
            byte[] working = baseImage ?? PngCodec.CreatePlaceholderPng(512, 512);

            if (pixelBackup)
            {
                byte[] result;
                if (TryEmbedPixelsIntoPng(working, payload, out result))
                {
                    working = result;
                }
                else
                {
                    Debug.LogWarning("[ModelCard] Payload does not fit in the base image's pixels; using a 1024x1024 placeholder instead.");
                    byte[] bigger = PngCodec.CreatePlaceholderPng(1024, 1024);
                    if (!TryEmbedPixelsIntoPng(bigger, payload, out result))
                    {
                        throw new InvalidDataException("Payload too large for LSB pixel embedding (max ~512 KB on 1024x1024).");
                    }
                    working = result;
                }
            }

            return PngCodec.EmbedChunk(working, payload);
        }

        private static bool TryEmbedPixelsIntoPng(byte[] png, byte[] payload, out byte[] result)
        {
            result = null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(png)) return false;
                Color32[] pixels = tex.GetPixels32();
                string error;
                if (!PngCodec.TryEmbedPixels(pixels, payload, out error)) return false;
                tex.SetPixels32(pixels);
                result = tex.EncodeToPNG();
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>Load a card from PNG bytes: chunk channel first, pixel backup second.</summary>
        public static ModelCardData LoadCardFromPng(byte[] png)
        {
            byte[] payload = PngCodec.ExtractChunk(png);
            if (payload != null)
            {
                var card = DecodePayload(payload);
                if (card != null) return card;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (tex.LoadImage(png))
                {
                    byte[] pixelPayload = PngCodec.ExtractPixels(tex.GetPixels32());
                    if (pixelPayload != null)
                    {
                        var card = DecodePayload(pixelPayload);
                        if (card != null) return card;
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            return null;
        }

        /// <summary>
        /// Capture the current Scene view into a PNG (used as card art without
        /// leaving the editor). Returns false when no Scene view is available.
        /// </summary>
        public static bool TryCaptureSceneView(out byte[] png)
        {
            png = null;
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null) return false;
            Camera cam = sv.camera;
            int w = Mathf.Max(1, cam.pixelWidth);
            int h = Mathf.Max(1, cam.pixelHeight);
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
            return png != null;
        }

        // --------------------------------------------------------------- helpers
        public static string GetRelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            while (t != null && t != root)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return parts.Count == 0 ? "" : string.Join("/", parts.ToArray());
        }

        /// <summary>
        /// Exact path lookup first; if the hierarchy changed, fall back to a unique
        /// name match on the last path segment (returns null when ambiguous).
        /// </summary>
        public static Transform FindTransform(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            Transform direct = root.Find(path);
            if (direct != null) return direct;

            string last = path.Substring(path.LastIndexOf('/') + 1);
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            Transform found = null;
            foreach (var c in all)
            {
                if (c.name != last) continue;
                if (found != null) return null; // ambiguous name
                found = c;
            }
            return found;
        }

        public static SkinnedMeshRenderer FindRenderer(Transform root, string path)
        {
            Transform t = FindTransform(root, path);
            return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Avatar";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }
}
