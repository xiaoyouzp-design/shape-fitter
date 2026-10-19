using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// Bakes a ModelCard into a VRChat FX AnimatorController so the look can be
    /// toggled at runtime with a single bool parameter ("ModelCard"), without
    /// permanently editing the prefab.
    ///
    /// The controller/clip assets are always generated. VRChat SDK wiring is
    /// attempted at runtime through reflection, so this file compiles cleanly even
    /// when the SDK is absent or not referenced by the current assembly. Any
    /// wiring failure only degrades to "assign the controller manually" — via the
    /// avatar's FX layer, Modular Avatar ("MA Animator") or VRCFury.
    ///
    /// The generated state uses writeDefaultValues = true, so exiting the state
    /// restores the avatar's prefab appearance.
    /// </summary>
    public static class ModelCardFxGenerator
    {
        public const string ParamName = "ModelCard";
        private const string OutputFolder = "Assets/形态建拟合器生成资源";

        public static bool Generate(GameObject root, ModelCardData card, out string error)
        {
            error = null;
            try
            {
                if (card.meshes.Count == 0 && card.bones.Count == 0)
                {
                    error = "The card is empty (no blend shapes or bones).";
                    return false;
                }

                EnsureFolder(OutputFolder);
                string name = ModelCardIO.SanitizeFileName(card.avatarName);
                string clipPath = AssetDatabase.GenerateUniqueAssetPath(OutputFolder + "/" + name + "_card.anim");
                string controllerPath = AssetDatabase.GenerateUniqueAssetPath(OutputFolder + "/" + name + "_card.controller");

                var clip = new AnimationClip { frameRate = 60 };
                AddShapeCurves(clip, card);
                AddBoneCurves(clip, card);
                AssetDatabase.CreateAsset(clip, clipPath);

                var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                controller.AddParameter(ParamName, AnimatorControllerParameterType.Bool);

                var sm = controller.layers[0].stateMachine;
                var baseState = sm.defaultState;
                if (baseState == null)
                {
                    baseState = sm.AddState("Base");
                    sm.defaultState = baseState;
                }

                var cardState = sm.AddState("Card");
                cardState.motion = clip;
                cardState.writeDefaultValues = true;

                var on = sm.AddAnyStateTransition(cardState);
                on.hasExitTime = false;
                on.duration = 0f;
                on.AddCondition(AnimatorConditionMode.If, 0f, ParamName);

                var off = cardState.AddTransition(baseState);
                off.hasExitTime = false;
                off.duration = 0f;
                off.AddCondition(AnimatorConditionMode.IfNot, 0f, ParamName);

                AssetDatabase.SaveAssets();

                TryWireToVrcSdk(root, controller, name);

                EditorGUIUtility.PingObject(controller);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ curves
        private static void AddShapeCurves(AnimationClip clip, ModelCardData card)
        {
            foreach (var m in card.meshes)
            {
                foreach (var s in m.shapes)
                {
                    if (Mathf.Abs(s.value) < 0.001f) continue;
                    var binding = EditorCurveBinding.FloatCurve(m.path, typeof(SkinnedMeshRenderer), "blendShape." + s.name);
                    AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f, s.value));
                }
            }
        }

        private static void AddBoneCurves(AnimationClip clip, ModelCardData card)
        {
            foreach (var b in card.bones)
            {
                if (b.localPosition == Vector3.zero && b.localRotation == Quaternion.identity && b.localScale == Vector3.one)
                    continue;
                SetCurve(clip, b.path, "m_LocalPosition.x", b.localPosition.x);
                SetCurve(clip, b.path, "m_LocalPosition.y", b.localPosition.y);
                SetCurve(clip, b.path, "m_LocalPosition.z", b.localPosition.z);
                SetCurve(clip, b.path, "m_LocalRotation.x", b.localRotation.x);
                SetCurve(clip, b.path, "m_LocalRotation.y", b.localRotation.y);
                SetCurve(clip, b.path, "m_LocalRotation.z", b.localRotation.z);
                SetCurve(clip, b.path, "m_LocalRotation.w", b.localRotation.w);
                SetCurve(clip, b.path, "m_LocalScale.x", b.localScale.x);
                SetCurve(clip, b.path, "m_LocalScale.y", b.localScale.y);
                SetCurve(clip, b.path, "m_LocalScale.z", b.localScale.z);
            }
        }

        private static void SetCurve(AnimationClip clip, string path, string property, float value)
        {
            var binding = EditorCurveBinding.FloatCurve(path, typeof(Transform), property);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f, value));
        }

        // ---------------------------------------------------------------- folders
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        // ------------------------------------------- VRC SDK wiring via reflection
        // Compile-safe without the SDK: everything below only touches the SDK
        // through Type.GetType + reflection, and any failure is caught and turned
        // into a "wire it manually" warning.
        private static void TryWireToVrcSdk(GameObject root, AnimatorController controller, string baseName)
        {
            try
            {
                Type descType = Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRC.SDK3.Avatars");
                if (descType == null)
                {
                    Debug.LogWarning("[ModelCard] VRChat SDK3 not detected - assign the generated FX controller manually, or via Modular Avatar / VRCFury.");
                    return;
                }
                Component descriptor = root.GetComponent(descType);
                if (descriptor == null)
                {
                    Debug.LogWarning("[ModelCard] VRCAvatarDescriptor not found on the avatar root - assign the generated FX controller manually.");
                    return;
                }

                // customExpressions = true
                var ce = descType.GetProperty("customExpressions");
                if (ce != null && ce.CanWrite) ce.SetValue(descriptor, true, null);

                // expression parameters asset (only when the avatar has none yet)
                var epProp = descType.GetProperty("expressionParameters");
                if (epProp != null && epProp.GetValue(descriptor, null) == null)
                {
                    Type epType = Type.GetType("VRC.SDK3.Avatars.Components.VRCExpressionParameters, VRC.SDK3.Avatars");
                    if (epType != null)
                    {
                        Type paramType = epType.GetNestedType("Parameter");
                        Type valueTypeEnum = epType.GetNestedType("ValueType");
                        if (paramType != null && valueTypeEnum != null)
                        {
                            var param = Activator.CreateInstance(paramType);
                            SetField(paramType, param, "name", ParamName);
                            SetField(paramType, param, "valueType", Enum.Parse(valueTypeEnum, "Bool"));
                            SetField(paramType, param, "defaultValue", 0f);
                            SetField(paramType, param, "saved", false);
                            SetField(paramType, param, "networkSynced", false);
                            var arr = Array.CreateInstance(paramType, 1);
                            arr.SetValue(param, 0);
                            var instance = ScriptableObject.CreateInstance(epType);
                            SetField(epType, instance, "parameters", arr);
                            string paramPath = OutputFolder + "/" + ModelCardIO.SanitizeFileName(baseName) + "_card.params.asset";
                            paramPath = AssetDatabase.GenerateUniqueAssetPath(paramPath);
                            AssetDatabase.CreateAsset(instance, paramPath);
                            epProp.SetValue(descriptor, instance, null);
                        }
                    }
                }

                // FX layer assignment (only when the FX layer is still the default)
                var layersProp = descType.GetProperty("baseAnimationLayers");
                if (layersProp != null)
                {
                    var layers = layersProp.GetValue(descriptor, null) as Array;
                    if (layers != null && layers.Length > 0)
                    {
                        Type elemType = layers.GetType().GetElementType();
                        var typeField = elemType != null ? elemType.GetField("type") : null;
                        var isDefaultField = elemType != null ? elemType.GetField("isDefault") : null;
                        var controllerField = elemType != null ? elemType.GetField("animatorController") : null;
                        bool modified = false;
                        for (int i = 0; i < layers.Length; i++)
                        {
                            var elem = layers.GetValue(i);
                            if (elem == null) continue;
                            object typeValue = typeField != null ? typeField.GetValue(elem) : null;
                            if (typeValue == null || typeValue.ToString() != "FX") continue;
                            if (isDefaultField != null && controllerField != null)
                            {
                                if ((bool)isDefaultField.GetValue(elem))
                                {
                                    isDefaultField.SetValue(elem, false);
                                    controllerField.SetValue(elem, controller);
                                    layers.SetValue(elem, i);
                                    modified = true;
                                }
                                else
                                {
                                    Debug.LogWarning("[ModelCard] The avatar already has a custom FX controller; the generated one was NOT assigned. Use Modular Avatar or merge manually.");
                                }
                            }
                        }
                        if (modified) layersProp.SetValue(descriptor, layers, null);
                    }
                }

                EditorUtility.SetDirty(descriptor);
                AssetDatabase.SaveAssets();
                Debug.Log("[ModelCard] FX layer wired: parameter '" + ParamName + "' toggles the look in-game.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ModelCard] Could not wire the VRChat SDK automatically (" + e.Message + "). Assign the generated FX controller manually, or via Modular Avatar / VRCFury.");
            }
        }

        private static void SetField(Type type, object target, string fieldName, object value)
        {
            var f = type.GetField(fieldName);
            if (f != null) f.SetValue(target, value);
        }
    }
}
