using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class CommonCoordinateTerm { public string point; public int axis; public double coefficient; }
    [Serializable] public class CommonCoordinateDefinition
    {
        public string id, label_zh, region, dimension;
        public CommonCoordinateTerm[] terms;
        public double[] display_range;
    }
    [Serializable] public class CommonCoordinateDefinitions
    {
        public int schema;
        public CommonCoordinateDefinition[] definitions;
        public static string Find(string name)
        {
            string file = name + ".json";
            var paths = AssetDatabase.FindAssets(name).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileName(p) == file).ToArray();
            if (paths.Length != 1) throw new InvalidOperationException("配置必须唯一：" + file + "，找到 " + paths.Length + " 份。");
            return paths[0];
        }
        public static CommonCoordinateDefinitions Load(out string hash)
        {
            var bytes = File.ReadAllBytes(Find("CommonCoordinateDefinitions"));
            hash = PhotoLandmarkTemplate.Hash(bytes);
            var result = JsonUtility.FromJson<CommonCoordinateDefinitions>(Encoding.UTF8.GetString(bytes));
            if (result == null || result.schema != 1 || result.definitions == null || result.definitions.Length != 52)
                throw new InvalidOperationException("共同坐标定义无效。");
            var names = new HashSet<string>();
            foreach (var d in result.definitions)
                if (string.IsNullOrEmpty(d.id) || !names.Add(d.id) || d.terms == null || d.terms.Length == 0
                    || d.display_range == null || d.display_range.Length != 2 || d.display_range[1] <= d.display_range[0]
                    || d.terms.Any(t => t.axis < 0 || t.axis > 2 || !Finite(t.coefficient)))
                    throw new InvalidOperationException("无效的共同坐标轴。");
            return result;
        }
        public static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
        public double[] Measure(double[,] anchors, string[] names)
        {
            var lookup = names.Select((n,i) => new {n,i}).ToDictionary(p => p.n,p => p.i);
            return definitions.Select(d => d.terms.Sum(t => anchors[lookup[t.point],t.axis] * t.coefficient)).ToArray();
        }
        public double Control(int i, double value)
        {
            var range = definitions[i].display_range;
            return 100 * (value - range[0]) / (range[1] - range[0]);
        }
    }

    [Serializable] public class CommonCoordinateProfile
    {
        public int schema, vertexCount, blendShapeCount;
        public string id, description, topologySha256, shapeNamesSha256, definitionSha256, provenance;
        public string generationMethod;
        public bool automaticReviewRequired,automaticReviewed;
        public string[] manuallyAdjustedPoints;
        public string[] keys;
        public PhotoLandmarkDefinition[] points;
        public bool Matches(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount != vertexCount || mesh.blendShapeCount != blendShapeCount) return false;
            var indices = mesh.triangles; var bytes = new byte[indices.Length * 4];
            Buffer.BlockCopy(indices,0,bytes,0,bytes.Length);
            if (PhotoLandmarkTemplate.Hash(bytes) != topologySha256) return false;
            return PhotoLandmarkTemplate.Hash(Encoding.UTF8.GetBytes(string.Join("|", Enumerable.Range(0,mesh.blendShapeCount).Select(mesh.GetBlendShapeName)))) == shapeNamesSha256;
        }
        public void Validate(string definitionHash)
        {
            if(automaticReviewRequired&&!automaticReviewed)throw new InvalidOperationException("自动迁移坐标尚未检查，请在构建窗口检查并确认候选点。");
            if ((schema!=1 && schema!=2) || string.IsNullOrEmpty(id) || keys==null || keys.Length==0 || points==null || definitionSha256!=definitionHash)
                throw new InvalidOperationException("模型配置版本、候选键或共同坐标定义无效。");
            var canonical=PhotoLandmarkTemplate.Load().points.Select(p=>p.id).ToArray();
            if(keys.Any(string.IsNullOrEmpty) || keys.Distinct().Count()!=keys.Length || points.Length!=canonical.Length || !points.Select(p=>p.id).OrderBy(n=>n).SequenceEqual(canonical.OrderBy(n=>n))
                || points.Any(p=>p.vertices==null || p.weights==null || p.vertices.Length==0 || p.vertices.Length!=p.weights.Length || p.vertices.Any(v=>v<0 || v>=vertexCount)
                    || p.weights.Any(w=>!CommonCoordinateDefinitions.Finite(w)||w<0) || Math.Abs(p.weights.Sum()-1)>1e-4))
                throw new InvalidOperationException("需要完整且唯一的 42 个语义点、有效顶点权重和唯一候选键。");
            points=canonical.Select(id=>points.First(p=>p.id==id)).ToArray();
        }
        public static CommonCoordinateProfile Bind(GameObject root, string definitionHash, out SkinnedMeshRenderer renderer, CommonCoordinateProfile selected=null)
        {
            if (root == null) throw new InvalidOperationException("请选择源模型和目标模型根对象。");
            var matches = new List<KeyValuePair<CommonCoordinateProfile,SkinnedMeshRenderer>>();
            var profiles=new List<CommonCoordinateProfile>();
            if(selected!=null)profiles.Add(selected);
            else profiles.AddRange(CommonCoordinateProfileStore.DiscoverProfiles(definitionHash));
            foreach(var p in profiles)
            {
                foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (p.Matches(r.sharedMesh)){p.Validate(definitionHash);matches.Add(new KeyValuePair<CommonCoordinateProfile,SkinnedMeshRenderer>(p,r));}
            }
            if (matches.Count != 1) throw new InvalidOperationException("找到 "+matches.Count+" 个匹配配置。未配置模型请先打开“构建模型坐标”；多个匹配请指定坐标配置或缩小模型根对象范围。");
            renderer = matches[0].Value;
            return matches[0].Key;
        }
    }

    // A read-only capture of actual current bones and BlendShapes. CPU skinning
    // is checked against Unity BakeMesh; no original weight is ever changed.
    public class CommonCoordinateModel
    {
        public CommonCoordinateProfile profile;
        public SkinnedMeshRenderer renderer;
        public double[] reference;
        public int[] indices;
        public Vector3[] currentVertices;
        public Vector3[][] deltas;
        public double[,] anchors;
        public double[,,] anchorDeltas;
        public Vector3 center;
        public double scale, bakeMaxError;
        public string[] Names { get { return profile.points.Select(p=>p.id).ToArray(); } }
        public static CommonCoordinateModel Capture(GameObject root, string definitionHash, CommonCoordinateProfile selected=null)
        {
            var m = new CommonCoordinateModel();
            m.profile = CommonCoordinateProfile.Bind(root, definitionHash, out m.renderer,selected);
            if(m.profile.schema==2)return CommonCoordinateCalibration.Capture(m.profile,m.renderer);
            var mesh = m.renderer.sharedMesh;
            m.indices = m.profile.keys.Select(mesh.GetBlendShapeIndex).ToArray();
            if (m.indices.Any(i=>i<0)) throw new InvalidOperationException("模型缺少配置的形态键。");
            m.reference = m.indices.Select(i=>(double)m.renderer.GetBlendShapeWeight(i)).ToArray();
            if (m.reference.Any(w=>w<0 || w>100 || !CommonCoordinateDefinitions.Finite(w)))
                throw new InvalidOperationException("当前候选键必须在 0–100 范围内，当前权重不会被自动截断。");
            var rootTransform = m.renderer.transform.parent;
            if (rootTransform == null) throw new InvalidOperationException("Body 必须位于模型根对象下。");
            var bones = m.renderer.bones; var binds = mesh.bindposes;
            var boneMatrices = binds.Select((b,i) => i < bones.Length && bones[i] != null
                ? rootTransform.worldToLocalMatrix * bones[i].localToWorldMatrix * b : Matrix4x4.zero).ToArray();
            var bw = mesh.boneWeights; var vertices = mesh.vertices;
            if (bw.Length != vertices.Length) throw new InvalidOperationException("不支持此模型的蒙皮格式。");
            var skin = new Matrix4x4[vertices.Length];
            var neutral = new Vector3[vertices.Length];
            for (int v=0;v<vertices.Length;v++)
            {
                var weight=bw[v]; var ids=new[]{weight.boneIndex0,weight.boneIndex1,weight.boneIndex2,weight.boneIndex3};
                var amounts=new[]{weight.weight0,weight.weight1,weight.weight2,weight.weight3}; var matrix=Matrix4x4.zero;
                for(int j=0;j<4;j++) if(amounts[j]>0)
                {
                    if(ids[j]>=boneMatrices.Length) throw new InvalidOperationException("骨骼索引超出范围。");
                    for(int q=0;q<16;q++) matrix[q]+=amounts[j]*boneMatrices[ids[j]][q];
                }
                skin[v]=matrix; neutral[v]=matrix.MultiplyPoint3x4(vertices[v]);
            }
            m.currentVertices=(Vector3[])neutral.Clone();
            var dv=new Vector3[vertices.Length]; var dn=new Vector3[vertices.Length]; var dt=new Vector3[vertices.Length];
            for(int k=0;k<mesh.blendShapeCount;k++)
            {
                float w=m.renderer.GetBlendShapeWeight(k);
                if(w==0)continue;
                if(mesh.GetBlendShapeFrameCount(k)!=1 || mesh.GetBlendShapeFrameWeight(k,0)<=0)
                    throw new InvalidOperationException("本原型仅支持单个正权重帧的形态键。");
                mesh.GetBlendShapeFrameVertices(k,0,dv,dn,dt); float factor=w/mesh.GetBlendShapeFrameWeight(k,0);
                for(int v=0;v<vertices.Length;v++)m.currentVertices[v]+=skin[v].MultiplyVector(dv[v])*factor;
            }
            var baked=new Mesh();
            try
            {
                m.renderer.BakeMesh(baked); var actual=baked.vertices;
                var toRoot=rootTransform.worldToLocalMatrix*m.renderer.transform.localToWorldMatrix;
                for(int v=0;v<vertices.Length;v++)m.bakeMaxError=Math.Max(m.bakeMaxError,(toRoot.MultiplyPoint3x4(actual[v])-m.currentVertices[v]).magnitude);
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
            if(m.bakeMaxError>5e-5)throw new InvalidOperationException("CPU 蒙皮与 BakeMesh 不一致："+m.bakeMaxError+"。当前姿态或蒙皮不受支持。");
            Func<string,Vector3> point=id=>m.Point(neutral,m.profile.points.First(p=>p.id==id));
            m.scale=Math.Abs(point("right_face_cheek").x-point("left_face_cheek").x);
            if(m.scale<1e-6)throw new InvalidOperationException("基准脸宽无效。");
            m.center=new Vector3(0,point("chin").y,point("nose_tip").z);
            m.deltas=new Vector3[m.indices.Length][];
            m.anchors=m.Anchors(m.currentVertices);
            m.anchorDeltas=new double[m.indices.Length,m.profile.points.Length,3];
            for(int k=0;k<m.indices.Length;k++)
            {
                int index=m.indices[k];
                if(mesh.GetBlendShapeFrameCount(index)!=1 || mesh.GetBlendShapeFrameWeight(index,0)<=0)
                    throw new InvalidOperationException("候选键不是单个正权重帧："+m.profile.keys[k]);
                mesh.GetBlendShapeFrameVertices(index,0,dv,dn,dt); float frame=mesh.GetBlendShapeFrameWeight(index,0);
                m.deltas[k]=new Vector3[vertices.Length];
                for(int v=0;v<vertices.Length;v++)m.deltas[k][v]=skin[v].MultiplyVector(dv[v])/frame;
                for(int p=0;p<m.profile.points.Length;p++)
                {
                    var d=m.Point(m.deltas[k],m.profile.points[p]);
                    m.anchorDeltas[k,p,0]=-d.x/m.scale;m.anchorDeltas[k,p,1]=d.y/m.scale;m.anchorDeltas[k,p,2]=-d.z/m.scale;
                }
            }
            return m;
        }
        private Vector3 Point(Vector3[] vertices,PhotoLandmarkDefinition p)
        {
            Vector3 a=Vector3.zero; for(int i=0;i<p.vertices.Length;i++)a+=vertices[p.vertices[i]]*p.weights[i];return a;
        }
        public double[,] Anchors(Vector3[] vertices)
        {
            var result=new double[profile.points.Length,3];
            for(int p=0;p<profile.points.Length;p++)
            {
                var a=Point(vertices,profile.points[p])-center;
                result[p,0]=-a.x/scale;result[p,1]=a.y/scale;result[p,2]=-a.z/scale;
            }
            return result;
        }
        public double[,] Predict(double[] w)
        {
            var a=(double[,])anchors.Clone();
            for(int k=0;k<w.Length;k++)for(int p=0;p<profile.points.Length;p++)for(int c=0;c<3;c++)a[p,c]+=anchorDeltas[k,p,c]*(w[k]-reference[k]);
            return a;
        }
        public Vector3[] MeshVertices(double[] w)
        {
            var vertices=(Vector3[])currentVertices.Clone();
            for(int k=0;k<w.Length;k++)for(int v=0;v<vertices.Length;v++)vertices[v]+=deltas[k][v]*(float)(w[k]-reference[k]);
            return vertices;
        }
        public Vector3[] PreviewVertices(double[] w)
        {
            return MeshVertices(w).Select(v=>new Vector3((float)(-(v.x-center.x)/scale),(float)((v.y-center.y)/scale),(float)(-(v.z-center.z)/scale))).ToArray();
        }
    }
}
