using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrcModelCard
{
    // Bake explicitly: an immediate camera render after SetBlendShapeWeight can
    // otherwise read the previous GPU skinning frame. Never clone avatar scripts.
    public static class FitPreviewCapture
    {
        public static Texture2D Capture(GameObject avatar, Camera framing, int width, int height)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var ownedMeshes = new List<Mesh>();
            try
            {
                foreach (var source in avatar.GetComponentsInChildren<Renderer>(false))
                {
                    if (!source.enabled) continue;
                    Mesh mesh = null;
                    var skin = source as SkinnedMeshRenderer;
                    if (skin != null && skin.sharedMesh != null)
                    {
                        mesh = new Mesh(); ownedMeshes.Add(mesh);
                        skin.BakeMesh(mesh);
                        mesh.RecalculateBounds();
                    }
                    else
                    {
                        var filter = source.GetComponent<MeshFilter>();
                        if (filter != null) mesh = filter.sharedMesh;
                    }
                    if (mesh == null) continue;
                    var go = new GameObject("__FitPreviewMesh__");
                    SceneManager.MoveGameObjectToScene(go, scene);
                    go.hideFlags = HideFlags.HideAndDontSave; go.layer = 31;
                    go.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                    go.transform.localScale = source.transform.lossyScale;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = source.sharedMaterials;
                    renderer.shadowCastingMode = source.shadowCastingMode;
                    renderer.receiveShadows = source.receiveShadows;
                    var block = new MaterialPropertyBlock();
                    source.GetPropertyBlock(block); renderer.SetPropertyBlock(block);
                    for (int i = 0; i < source.sharedMaterials.Length; i++)
                    {
                        block.Clear(); source.GetPropertyBlock(block, i); renderer.SetPropertyBlock(block, i);
                    }
                }
                foreach (var light in Object.FindObjectsOfType<Light>())
                {
                    if (!light.enabled || light.gameObject.scene != avatar.scene) continue;
                    var go = new GameObject("__FitPreviewLight__");
                    SceneManager.MoveGameObjectToScene(go, scene);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.transform.SetPositionAndRotation(light.transform.position, light.transform.rotation);
                    var copy = go.AddComponent<Light>(); EditorUtility.CopySerialized(light, copy);
                    copy.cullingMask = 1 << 31;
                }
                var cameraObject = new GameObject("__FitPreviewCamera__");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                cameraObject.hideFlags = HideFlags.HideAndDontSave;
                var camera = cameraObject.AddComponent<Camera>();
                camera.CopyFrom(framing);
                camera.transform.SetPositionAndRotation(framing.transform.position, framing.transform.rotation);
                camera.enabled = false; camera.targetTexture = null; camera.cullingMask = 1 << 31;
                camera.scene = scene; camera.aspect = (float)width / height;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.15f, .16f, .18f, 1f);
                RenderTexture previous = RenderTexture.active;
                try { return AvatarFitRenderer.RenderWith(camera, width, height, null); }
                finally { RenderTexture.active = previous; }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var mesh in ownedMeshes) Object.DestroyImmediate(mesh);
            }
        }
    }
}
