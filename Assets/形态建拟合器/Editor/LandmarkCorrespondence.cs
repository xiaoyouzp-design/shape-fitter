using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Landmark pairs for fit-from-image: a point on the reference image paired
    /// with a point on the avatar render. Render-side points are anchored to mesh
    /// vertices (bake + ray-triangle at click time) so they follow deformation
    /// during fitting.
    /// </summary>
    public class LandmarkSet
    {
        public class Anchor
        {
            public SkinnedMeshRenderer smr;
            public int vertexIndex;
            public string semanticId;
            public int[] vertexIndices;
            public float[] vertexWeights;

            public Vector3 Position(Vector3[] vertices)
            {
                if (vertexIndices == null) return vertices[vertexIndex];
                Vector3 point = Vector3.zero;
                for (int i = 0; i < vertexIndices.Length; i++) point += vertices[vertexIndices[i]] * vertexWeights[i];
                return point;
            }
            public bool useWorldSpace; // true when BakeMesh output is world space
            public Vector2 renderPixel; // where it was clicked (render pixel space)
        }

        public readonly System.Collections.Generic.List<Vector2> imagePoints = new System.Collections.Generic.List<Vector2>();
        public readonly System.Collections.Generic.List<Anchor> renderAnchors = new System.Collections.Generic.List<Anchor>();

        public int Count
        {
            get { return Mathf.Min(imagePoints.Count, renderAnchors.Count); }
        }
    }

    /// <summary>Ray-casting helpers that anchor render-pane clicks to mesh vertices.</summary>
    public static class LandmarkCorrespondence
    {
        /// <summary>
        /// Pick in the preview's pixel coordinates even after its temporary
        /// render target has been released. ScreenPointToRay and the verification
        /// projection must use the same target dimensions as the displayed render.
        /// </summary>
        public static bool TryAnchorAtPixel(Camera cam, GameObject root, Vector2 pixel,
            int renderWidth, int renderHeight, out LandmarkSet.Anchor anchor)
        {
            anchor = null;
            if (cam == null || root == null || renderWidth <= 0 || renderHeight <= 0) return false;
            RenderTexture previousTarget = cam.targetTexture;
            var pickTarget = RenderTexture.GetTemporary(renderWidth, renderHeight, 0);
            try
            {
                // Only bind the dimensions; no extra camera render is needed.
                cam.targetTexture = pickTarget;
                return TryAnchorAtPixel(cam, root, pixel, out anchor);
            }
            finally
            {
                cam.targetTexture = previousTarget;
                RenderTexture.ReleaseTemporary(pickTarget);
            }
        }

        /// <summary>
        /// Convert a click pixel (render texture space, y-up) into a vertex anchor.
        /// Both possible BakeMesh spaces (local vs world) are tried and the result
        /// is verified by re-projecting the vertex.
        /// </summary>
        public static bool TryAnchorAtPixel(Camera cam, GameObject root, Vector2 pixel, out LandmarkSet.Anchor anchor)
        {
            anchor = null;
            Ray ray = cam.ScreenPointToRay(new Vector3(pixel.x, pixel.y, 0f));
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                if (smr.sharedMesh == null) continue;
                LandmarkSet.Anchor hit;
                if (TryHit(smr, ray, true, out hit))
                {
                    anchor = hit;
                    VerifyAndFix(cam, pixel, anchor);
                    return true;
                }
                if (TryHit(smr, ray, false, out hit))
                {
                    anchor = hit;
                    VerifyAndFix(cam, pixel, anchor);
                    return true;
                }
            }
            return false;
        }

        private static bool TryHit(SkinnedMeshRenderer smr, Ray ray, bool localToWorld, out LandmarkSet.Anchor anchor)
        {
            anchor = null;
            var baked = new Mesh();
            try
            {
                smr.BakeMesh(baked);
                Vector3[] verts = baked.vertices;
                int[] tris = baked.triangles;
                float bestDist = float.MaxValue;
                int bestV = -1;
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 a = localToWorld ? smr.transform.TransformPoint(verts[tris[i]]) : verts[tris[i]];
                    Vector3 b = localToWorld ? smr.transform.TransformPoint(verts[tris[i + 1]]) : verts[tris[i + 1]];
                    Vector3 c = localToWorld ? smr.transform.TransformPoint(verts[tris[i + 2]]) : verts[tris[i + 2]];
                    float dist;
                    Vector3 bary;
                    if (RayTriangle(ray, a, b, c, out dist, out bary) && dist < bestDist)
                    {
                        bestDist = dist;
                        bestV = bary.x >= bary.y && bary.x >= bary.z ? tris[i] : (bary.y >= bary.z ? tris[i + 1] : tris[i + 2]);
                    }
                }
                if (bestV >= 0)
                {
                    anchor = new LandmarkSet.Anchor { smr = smr, vertexIndex = bestV, useWorldSpace = !localToWorld };
                    return true;
                }
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// Project the anchored vertex back into the render and pick the BakeMesh
        /// space (local vs world) whose projection matches the clicked pixel.
        /// </summary>
        private static void VerifyAndFix(Camera cam, Vector2 pixel, LandmarkSet.Anchor a)
        {
            var baked = new Mesh();
            try
            {
                a.smr.BakeMesh(baked);
                Vector3 world = a.useWorldSpace ? baked.vertices[a.vertexIndex] : a.smr.transform.TransformPoint(baked.vertices[a.vertexIndex]);
                Vector3 sp = cam.WorldToScreenPoint(world);
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), pixel);
                if (d > 12f && !a.useWorldSpace)
                {
                    Vector3 world2 = baked.vertices[a.vertexIndex];
                    Vector3 sp2 = cam.WorldToScreenPoint(world2);
                    if (Vector2.Distance(new Vector2(sp2.x, sp2.y), pixel) < d) a.useWorldSpace = true;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        private static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float dist, out Vector3 bary)
        {
            dist = 0f;
            bary = Vector3.zero;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(ray.direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-8f) return false;
            float inv = 1f / det;
            Vector3 tvec = ray.origin - a;
            float u = Vector3.Dot(tvec, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(tvec, e1);
            float v = Vector3.Dot(ray.direction, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            dist = Vector3.Dot(e2, q) * inv;
            if (dist < 0f) return false;
            bary = new Vector3(u, v, 1f - u - v);
            return true;
        }
    }
}
