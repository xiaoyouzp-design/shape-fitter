using System;
using System.Collections.Generic;
using UnityEngine;

namespace VrcModelCard
{
    /// <summary>
    /// A "Model Card": a portable snapshot of an avatar's modification parameters
    /// (blend shapes + bone adjustments), inspired by Koikatsu character cards.
    ///
    /// The card is serialized with JsonUtility, deflate-compressed, and embedded
    /// inside a PNG image (custom "mcrd" chunk + optional LSB pixel backup) so the
    /// picture itself carries the whole avatar preset.
    /// </summary>
    [Serializable]
    public class ModelCardData
    {
        public const string FormatName = "VRC ModelCard";
        public const string FormatVersion = "1.0";
        public const string Magic = "MCARD1.0"; // must be exactly 8 ASCII chars

        public string format = FormatName;
        public string formatVersion = FormatVersion;
        public string avatarName = "";
        public string author = "";
        public string description = "";
        public string createdAt = "";
        public List<MeshAdjustment> meshes = new List<MeshAdjustment>();
        public List<BoneAdjustment> bones = new List<BoneAdjustment>();

        /// <summary>Total number of blend shape entries across all meshes.</summary>
        public int TotalShapes
        {
            get
            {
                int n = 0;
                foreach (var m in meshes) n += m.shapes.Count;
                return n;
            }
        }
    }

    [Serializable]
    public class MeshAdjustment
    {
        /// <summary>Transform path of the SkinnedMeshRenderer, relative to the avatar root.</summary>
        public string path = "";
        public List<ShapeAdjustment> shapes = new List<ShapeAdjustment>();
    }

    [Serializable]
    public class ShapeAdjustment
    {
        public string name = "";
        public float value;
    }

    [Serializable]
    public class BoneAdjustment
    {
        /// <summary>Transform path of the bone, relative to the avatar root.</summary>
        public string path = "";
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
    }
}
