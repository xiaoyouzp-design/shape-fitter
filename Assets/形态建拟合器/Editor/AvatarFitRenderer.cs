using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Editor-time avatar rendering for fit-from-image: an orthographic camera
    /// (front or mirrored view) renders ONLY the avatar onto a transparent
    /// RenderTexture by temporarily hijacking a layer. The returned camera stays
    /// alive for landmark projection and can be reused for fast re-renders.
    /// </summary>
    public static class AvatarFitRenderer
    {
        private const int HijackLayer = 31;

        /// <summary>
        /// Render the avatar to a transparent texture. On success the caller owns
        /// both the texture and the camera (destroy with DestroyImmediate).
        /// </summary>
        public static bool RenderAvatar(GameObject root, int width, int height, out Texture2D tex, out Camera cam, bool mirror = false, Camera template = null)
        {
            tex = null;
            cam = null;
            if (root == null) return false;
            Bounds bounds = GetBounds(root);
            if (bounds.size == Vector3.zero) return false;

            int[] layers = TemporarilySetLayer(root, HijackLayer);
            var camGo = new GameObject("__ModelCardFitCam__");
            camGo.hideFlags = HideFlags.HideAndDontSave;
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << HijackLayer;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 200f;

            if (template != null)
            {
                CopyCameraSettings(template, cam);
                // Match the destination RenderTexture ratio so the scene view
                // is not stretched when the source camera has another aspect.
                cam.aspect = (float)width / Mathf.Max(height, 1);
            }
            else
            {
                Vector3 center = bounds.center;
                Vector3 forward = mirror ? root.transform.forward : -root.transform.forward;
                cam.transform.position = center + forward * 20f;
                cam.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up);
                float aspect = (float)width / Mathf.Max(height, 1);
                float size = Mathf.Max(bounds.extents.y, bounds.extents.x / Mathf.Max(aspect, 0.01f)) * 1.15f;
                cam.orthographicSize = Mathf.Max(size, 0.01f);
                cam.aspect = aspect;
            }

            try
            {
                tex = RenderWith(cam, width, height, null);
            }
            finally
            {
                RestoreLayers(root, layers);
                if (tex == null)
                {
                    UnityEngine.Object.DestroyImmediate(camGo);
                    cam = null;
                }
            }
            return tex != null;
        }

        /// <summary>Copy the user-facing camera controls to the hidden preview camera.</summary>
        public static void CopyCameraSettings(Camera source, Camera destination)
        {
            if (source == null || destination == null) return;
            destination.transform.position = source.transform.position;
            destination.transform.rotation = source.transform.rotation;
            destination.orthographic = source.orthographic;
            destination.orthographicSize = source.orthographicSize;
            destination.fieldOfView = source.fieldOfView;
            destination.aspect = source.aspect;
            destination.nearClipPlane = source.nearClipPlane;
            destination.farClipPlane = source.farClipPlane;
            destination.usePhysicalProperties = source.usePhysicalProperties;
            if (source.usePhysicalProperties)
            {
                destination.focalLength = source.focalLength;
                destination.sensorSize = source.sensorSize;
                destination.lensShift = source.lensShift;
            }
        }

        public static void SetRenderAspect(Camera camera, int width, int height)
        {
            if (camera != null) camera.aspect = (float)width / Mathf.Max(height, 1);
        }

        /// <summary>Render the camera into a texture. Pass an existing texture to reuse it.</summary>
        public static Texture2D RenderWith(Camera cam, int width, int height, Texture2D reuse)
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                if (reuse == null || reuse.width != width || reuse.height != height)
                {
                    if (reuse != null) UnityEngine.Object.DestroyImmediate(reuse);
                    reuse = new Texture2D(width, height, TextureFormat.RGBA32, false);
                }
                RenderTexture.active = rt;
                reuse.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                reuse.Apply();
                RenderTexture.active = null;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
            return reuse;
        }

        /// <summary>Render an avatar while temporarily putting its hierarchy on the preview layer.</summary>
        public static Texture2D RenderWithAvatar(GameObject root, Camera cam, int width, int height, Texture2D reuse)
        {
            if (root == null) return reuse;
            int[] layers = TemporarilySetLayer(root, HijackLayer);
            try { return RenderWith(cam, width, height, reuse); }
            finally { RestoreLayers(root, layers); }
        }

        /// <summary>Alpha-based foreground mask (true = avatar pixel).</summary>
        public static bool[] GetMask(Texture2D tex, float alphaThreshold = 0.5f)
        {
            Color32[] colors = tex.GetPixels32();
            var mask = new bool[colors.Length];
            float cutoff = alphaThreshold * 255f;
            for (int i = 0; i < colors.Length; i++) mask[i] = colors[i].a >= cutoff;
            return mask;
        }

        public static Bounds GetBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds();
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        private static int[] TemporarilySetLayer(GameObject root, int layer)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            var layers = new int[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                layers[i] = all[i].gameObject.layer;
                all[i].gameObject.layer = layer;
            }
            return layers;
        }

        private static void RestoreLayers(GameObject root, int[] layers)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            int n = Mathf.Min(all.Length, layers.Length);
            for (int i = 0; i < n; i++) all[i].gameObject.layer = layers[i];
        }
    }
}
