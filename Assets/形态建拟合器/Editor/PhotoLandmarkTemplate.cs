using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class PhotoLandmarkDefinition
    {
        public string id, label;
        public int[] vertices;
        public float[] weights;
    }

    [Serializable] public class PhotoLandmarkTemplate
    {
        public const string SemanticId = "face-semantic-42-v1";
        public int schema, vertexCount, blendShapeCount;
        public string id, description, topologySha256, shapeNamesSha256;
        public PhotoLandmarkDefinition[] points;

        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static PhotoLandmarkTemplate Load()
        {
            string path = AssetDatabase.FindAssets("RuruneLandmarkTemplate")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("/RuruneLandmarkTemplate.json", StringComparison.Ordinal));
            if (path == null) throw new InvalidOperationException("未找到 Rurune 模型端模板，请重新安装插件模板文件。");
            var t = JsonUtility.FromJson<PhotoLandmarkTemplate>(File.ReadAllText(path));
            if (t == null || t.schema != 1 || t.points == null || t.points.Length == 0)
                throw new InvalidOperationException("模型端模板格式无效。");
            var names = new HashSet<string>();
            foreach (var p in t.points)
            {
                if (string.IsNullOrEmpty(p.id) || !names.Add(p.id) || p.vertices == null || p.weights == null
                    || p.vertices.Length != 3 || p.weights.Length != 3
                    || p.vertices.Any(i => i < 0 || i >= t.vertexCount)
                    || p.weights.Any(w => float.IsNaN(w) || float.IsInfinity(w) || w < 0)
                    || Mathf.Abs(p.weights.Sum() - 1) > .0001f)
                    throw new InvalidOperationException("模型端模板含无效的三角形锚点。");
            }
            return t;
        }

        public bool Matches(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount != vertexCount || mesh.blendShapeCount != blendShapeCount) return false;
            var indices = mesh.triangles;
            byte[] bytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
            if (Hash(bytes) != topologySha256) return false;
            var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName);
            return Hash(Encoding.UTF8.GetBytes(string.Join("|", names.ToArray()))) == shapeNamesSha256;
        }

        public SkinnedMeshRenderer Bind(GameObject root)
        {
            if (root == null) throw new InvalidOperationException("请选择 Rurune 模型根对象。");
            var matches = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => Matches(r.sharedMesh)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? "该模型不匹配内置 Rurune Body 模板；不会猜测顶点对应。请选择支持的模型。"
                    : "找到多个匹配的 Body，请选择只包含一个目标 Body 的根对象。");
            return matches[0];
        }

        // Template left/right is screen-relative. Avatar root.forward can point
        // either toward or away from the face, so derive the camera frame from
        // named model anchors, independently of which photo points are visible.
        public static void ConfigureCamera(GameObject root, Camera camera, int width, int height)
        {
            var template = Load();
            var renderer = template.Bind(root);
            ConfigureCamera(template, renderer, camera, width, height);
        }

        public static void ConfigureCamera(PhotoLandmarkTemplate template, SkinnedMeshRenderer renderer, Camera camera, int width, int height)
        {
            if (template == null || renderer == null || !template.Matches(renderer.sharedMesh))
                throw new InvalidOperationException("图片模型坐标与脸部网格不匹配。");
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh);
                Vector3[] vertices = mesh.vertices;
                Func<string, Vector3> point = id => {
                    var def = template.points.First(p => p.id == id);
                    Vector3 local = Vector3.zero;
                    for (int i = 0; i < def.vertices.Length; i++) local += vertices[def.vertices[i]] * def.weights[i];
                    return renderer.transform.TransformPoint(local);
                };
                Vector3 left = point("left_eye_outer"), right = point("right_eye_outer");
                Vector3 eyes = (left + right) * .5f, chin = point("chin");
                Vector3 x = right - left;
                Vector3 y = eyes - chin;
                if (x.sqrMagnitude < 1e-10f) throw new InvalidOperationException("模型模板眼部锚点重合，无法建立正面相机。");
                x.Normalize(); y -= Vector3.Dot(y, x) * x;
                if (y.sqrMagnitude < 1e-10f) throw new InvalidOperationException("模型模板锚点退化，无法建立正面相机。");
                y.Normalize();
                Vector3 forward = Vector3.Cross(x, y).normalized;
                Vector3 center = (eyes + chin) * .5f;
                float size = Mathf.Max(Vector3.Distance(eyes, chin), Vector3.Distance(left, right)) * .85f;
                camera.transform.SetPositionAndRotation(center - forward * Mathf.Max(size * 10f, 1f), Quaternion.LookRotation(forward, y));
                camera.orthographic = true;
                camera.aspect = (float)width / Mathf.Max(1, height);
                camera.orthographicSize = Mathf.Max(size, size / camera.aspect);
                camera.nearClipPlane = .001f;
                camera.farClipPlane = Mathf.Max(size * 30f, 100f);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }

        public LandmarkSet Build(SkinnedMeshRenderer renderer, PhotoAnnotation annotation, int width, int height)
        {
            var set = new LandmarkSet();
            foreach (var p in points)
            {
                var mark = annotation.points.Find(v => v.id == p.id);
                if (mark == null || mark.state != "marked") continue;
                set.imagePoints.Add(new Vector2(mark.x * (width - 1), (1 - mark.y) * (height - 1)));
                set.renderAnchors.Add(new LandmarkSet.Anchor {
                    smr = renderer, vertexIndex = p.vertices[0], vertexIndices = p.vertices,
                    vertexWeights = p.weights, semanticId = p.id, useWorldSpace = false
                });
            }
            return set;
        }
    }

    [Serializable] public class PhotoMark
    {
        public string id, state = "unmarked";
        public float x, y;
    }

    [Serializable] public class PhotoAnnotation
    {
        public int schema = 1, imageWidth, imageHeight;
        public string templateId, imageSha256;
        public string annotationNote;
        public string coordinateSystem = "normalized_xy_top_left";
        public List<PhotoMark> points = new List<PhotoMark>();
        public List<PhotoTraceRecord> contourTraces = new List<PhotoTraceRecord>();

        public void Set(string id, float x, float y, string state)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)
                || x < 0 || x > 1 || y < 0 || y > 1
                || (state != "marked" && state != "skipped" && state != "unmarked"))
                throw new InvalidOperationException("标注坐标或状态无效。");
            var p = points.Find(v => v.id == id);
            if (p == null) { p = new PhotoMark { id = id }; points.Add(p); }
            p.x = x; p.y = y; p.state = state;
        }

        public void Validate(PhotoLandmarkTemplate template, string hash, int width, int height)
        {
            // Photo semantics did not change; only the model-side binding was
            // corrected. Existing photo annotations may migrate without clicks.
            if ((templateId == "rurune-body-9014-v1" || templateId == "rurune-body-9014-v2")
                && (template.id == "rurune-body-9014-v2" || template.id == "rurune-body-9014-v3")) templateId = template.id;
            // Image semantic identities are shared across registered models;
            // mesh topology is validated separately when binding the profile.
            if (template.id == PhotoLandmarkTemplate.SemanticId &&
                (templateId == "rurune-body-9014-v1" || templateId == "rurune-body-9014-v2" || templateId == "rurune-body-9014-v3"))
                templateId = template.id;
            if(template.id==PhotoDetailLandmarks.SemanticId && (templateId==PhotoLandmarkTemplate.SemanticId || templateId=="rurune-body-9014-v1" || templateId=="rurune-body-9014-v2" || templateId=="rurune-body-9014-v3"))templateId=template.id;
            if (schema != 1 || templateId != template.id || imageSha256 != hash
                || imageWidth != width || imageHeight != height || coordinateSystem != "normalized_xy_top_left" || points == null)
                throw new InvalidOperationException("标注文件与当前图片或模型模板不匹配，请加载原图片。");
            var ids = new HashSet<string>();
            foreach (var p in points)
            {
                if (p == null || !ids.Add(p.id) || !template.points.Any(d => d.id == p.id))
                    throw new InvalidOperationException("标注文件含未知或重复特征点。");
                Set(p.id, p.x, p.y, p.state);
            }
        }
    }
}
