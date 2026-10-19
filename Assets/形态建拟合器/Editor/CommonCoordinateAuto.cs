using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class CoordinateAutoPoint
    {
        public string id,label;
        public float surfaceDistance;
        public bool needsAttention;
        public Vector3 normalizedPosition;
    }
    [Serializable] public class CoordinateAutoResult
    {
        public CommonCoordinateProfile profile;
        public string method,reference,explanation;
        public CoordinateAutoPoint[] diagnostics;
        public Vector3[] previewVertices;
    }
    public static class CommonCoordinateAuto
    {
        private class Surface
        {
            public Vector3[] local,normalized;
            public bool[] mask;
            public int[] triangles;
            public float width;
        }
        public static CommonCoordinateProfile FindProfile(SkinnedMeshRenderer face)
        {
            string hash;CommonCoordinateDefinitions.Load(out hash);
            var matches=new List<CommonCoordinateProfile>();
            foreach(var p in CommonCoordinateProfileStore.DiscoverProfiles(hash))
            {
                if(p==null||!p.Matches(face.sharedMesh))continue;
                try{p.Validate(hash);matches.Add(p);}catch{continue;}
            }
            if(matches.Count>1)throw new InvalidOperationException("同一网格有多份有效配置，请使用“载入配置”选择要复用的一份。");
            return matches.SingleOrDefault();
        }
        public static Transform Head(SkinnedMeshRenderer face)
        {
            foreach(var animator in face.GetComponentsInParent<Animator>(true))
                if(animator.isHuman){var head=animator.GetBoneTransform(HumanBodyBones.Head);if(head!=null)return head;}
            var candidates=face.bones.Where(b=>b!=null&&(b.name.Equals("head",StringComparison.OrdinalIgnoreCase)||b.name=="頭"||b.name.EndsWith("_Head",StringComparison.OrdinalIgnoreCase)||b.name.EndsWith(":Head",StringComparison.OrdinalIgnoreCase))).Distinct().ToArray();
            return candidates.Length==1?candidates[0]:null;
        }
        private static Surface Read(SkinnedMeshRenderer face,Transform suppliedHead)
        {
            if(face==null||face.sharedMesh==null)throw new InvalidOperationException("请选择脸部网格。");
            var head=suppliedHead!=null?suppliedHead:Head(face);
            if(head==null)throw new InvalidOperationException("未能确定头骨，请在自动设置中指定头骨 Transform；无需手点 42 个坐标。");
            var animator=face.GetComponentsInParent<Animator>(true).FirstOrDefault();
            var frame=animator!=null?animator.transform:face.transform.root;
            var mesh=new Mesh();
            try
            {
                face.BakeMesh(mesh);var local=mesh.vertices;var vertices=local.Select(v=>frame.InverseTransformPoint(face.transform.TransformPoint(v))).ToArray();
                var bones=face.bones;var bw=face.sharedMesh.boneWeights;
                if(bw.Length!=vertices.Length)throw new InvalidOperationException("自动头部区域需要可读取的骨骼权重。");
                var headBone=bones.Select(b=>b!=null&&(b==head||b.IsChildOf(head))).ToArray();var mask=new bool[vertices.Length];
                for(int i=0;i<mask.Length;i++)
                {
                    var w=bw[i];var ids=new[]{w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3};var amounts=new[]{w.weight0,w.weight1,w.weight2,w.weight3};float sum=0;
                    for(int j=0;j<4;j++)if(ids[j]>=0&&ids[j]<headBone.Length&&headBone[ids[j]])sum+=amounts[j];mask[i]=sum>=.5f;
                }
                var subset=vertices.Where((v,i)=>mask[i]).ToArray();if(subset.Length<30)throw new InvalidOperationException("头骨影响的网格点不足，请检查脸部网格及头骨选择。");
                var min=subset.Aggregate(Vector3.Min);var max=subset.Aggregate(Vector3.Max);var size=max-min;float width=size.x;
                if(width<1e-5||size.y<1e-5||size.z<1e-5)throw new InvalidOperationException("头部范围退化，无法自动对齐。");
                // Model-root X/Y/Z convention: front faces +Z. Transfer uses a
                // head-bound normalization, not a claim of anatomical recognition.
                var center=(min+max)*.5f;
                var normalized=vertices.Select(v=>new Vector3((v.x-center.x)/size.x,(v.y-center.y)/size.y,(v.z-center.z)/size.z)).ToArray();
                return new Surface{local=local,normalized=normalized,mask=mask,triangles=mesh.triangles,width=width};
            }
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }
        public static CoordinateAutoResult Generate(SkinnedMeshRenderer target,SkinnedMeshRenderer reference=null,Transform targetHead=null,bool forceTransfer=false,CommonCoordinateProfile preferredProfile=null)
        {
            if(target==null||target.sharedMesh==null)throw new InvalidOperationException("请选择脸部网格。");
            if(!forceTransfer)
            {
                var existing=preferredProfile;
                if(existing!=null)
                {
                    string hash;CommonCoordinateDefinitions.Load(out hash);
                    if(!existing.Matches(target.sharedMesh))throw new InvalidOperationException("已载入配置与当前网格不匹配。");existing.Validate(hash);
                }
                else existing=FindProfile(target);
                if(existing!=null)return new CoordinateAutoResult{profile=JsonUtility.FromJson<CommonCoordinateProfile>(JsonUtility.ToJson(existing)),method="exact_profile",reference=existing.id,
                    explanation="已按网格拓扑与形态键指纹复用已有对应关系，无需重新标点。可直接使用原配置进行模型拟合。",diagnostics=new CoordinateAutoPoint[0],previewVertices=new Vector3[0]};
            }
            CommonCoordinateProfile source=null;
            if(reference!=null)source=FindProfile(reference);
            else
            {
                foreach(var candidate in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true).Where(r=>r!=target&&r.gameObject.scene.IsValid()&&!EditorUtility.IsPersistent(r)).OrderBy(r=>r.name,StringComparer.Ordinal).ThenBy(r=>r.GetInstanceID()))
                {try{var p=FindProfile(candidate);if(p!=null&&Head(candidate)!=null){reference=candidate;source=p;break;}}catch(InvalidOperationException){}}
            }
            if(reference==null||source==null)throw new InvalidOperationException("场景中没有可用参考。请放入已配置模型，或在自动设置中指定它的脸部网格。");
            if(reference==target)throw new InvalidOperationException("新拓扑迁移需要另一个已配置的参考网格。");
            var src=Read(reference,null);var dst=Read(target,targetHead);var profile=CommonCoordinateBuilderWindow.NewProfile(target);
            var diagnostics=new List<CoordinateAutoPoint>();
            for(int p=0;p<profile.points.Length;p++)
            {
                var point=profile.points[p];var sourcePoint=source.points.First(a=>a.id==point.id);
                var predicted=CommonCoordinateCalibration.Point(src.normalized,sourcePoint);
                float best=float.PositiveInfinity;int[] ids=null;Vector3 bary=Vector3.zero,position=Vector3.zero;
                for(int t=0;t<dst.triangles.Length;t+=3)
                {
                    int a=dst.triangles[t],b=dst.triangles[t+1],c=dst.triangles[t+2];if(!dst.mask[a]||!dst.mask[b]||!dst.mask[c])continue;
                    Vector3 weights;var q=Closest(predicted,dst.normalized[a],dst.normalized[b],dst.normalized[c],out weights);float d=(q-predicted).sqrMagnitude;
                    if(d<best){best=d;ids=new[]{a,b,c};bary=weights;position=q;}
                }
                if(ids==null)throw new InvalidOperationException("没有可用头部三角形。");
                point.vertices=ids;point.weights=new[]{bary.x,bary.y,bary.z};
                diagnostics.Add(new CoordinateAutoPoint{id=point.id,label=point.label,surfaceDistance=Mathf.Sqrt(best),needsAttention=true,normalizedPosition=position});
            }
            profile.keys=SuggestKeys(target,profile,dst);
            profile.generationMethod="head_bounds_surface_transfer_v1";profile.automaticReviewRequired=true;profile.automaticReviewed=false;
            profile.provenance="Automatic candidate transfer from "+source.id+"; current posed mesh; rig-root axes and head bounds; nearest triangle barycentric projection; requires human review. Distance is not semantic confidence.";
            return new CoordinateAutoResult{profile=profile,method=profile.generationMethod,reference=source.id,diagnostics=diagnostics.ToArray(),previewVertices=dst.normalized.Where((v,i)=>dst.mask[i]&&i%3==0).Take(3000).ToArray(),
                explanation="42 点由参考配置迁移生成，尚未证明语义正确。请核对正面方向、眼睑／眉毛／嘴唇与脸廓；橙色为重点检查点。表面距离不是识别置信度。候选键按局部响应筛选，可能包含表情。"};
        }
        public static void RefreshPreview(CoordinateAutoResult result,SkinnedMeshRenderer target,Transform head=null)
        {
            if(result.diagnostics==null||result.diagnostics.Length==0)return;
            var surface=Read(target,head);
            foreach(var item in result.diagnostics)
                item.normalizedPosition=CommonCoordinateCalibration.Point(surface.normalized,result.profile.points.First(p=>p.id==item.id));
        }
        private static string[] SuggestKeys(SkinnedMeshRenderer face,CommonCoordinateProfile profile,Surface surface)
        {
            var mesh=face.sharedMesh;var delta=new Vector3[mesh.vertexCount];var choices=new List<KeyValuePair<string,float>>();
            for(int k=0;k<mesh.blendShapeCount;k++)
            {
                if(mesh.GetBlendShapeFrameCount(k)!=1||mesh.GetBlendShapeFrameWeight(k,0)<=0)continue;
                mesh.GetBlendShapeFrameVertices(k,0,delta,null,null);float magnitude=0,total=0,head=0;
                for(int i=0;i<delta.Length;i++){float v=delta[i].sqrMagnitude;total+=v;if(surface.mask[i])head+=v;}
                if(total<1e-16||head/total<.8f)continue;
                foreach(var p in profile.points)magnitude+=CommonCoordinateCalibration.Point(delta,p).sqrMagnitude;
                magnitude=Mathf.Sqrt(magnitude/profile.points.Length)*100/mesh.GetBlendShapeFrameWeight(k,0);
                if(magnitude>surface.width*.001f)choices.Add(new KeyValuePair<string,float>(mesh.GetBlendShapeName(k),magnitude));
            }
            return choices.OrderByDescending(v=>v.Value).ThenBy(v=>v.Key,StringComparer.Ordinal).Take(32).Select(v=>v.Key).ToArray();
        }
        // Closest point on a triangle (Ericson), including edges and vertices.
        public static Vector3 Closest(Vector3 p,Vector3 a,Vector3 b,Vector3 c,out Vector3 w)
        {
            var ab=b-a;var ac=c-a;
            if(Vector3.Cross(ab,ac).sqrMagnitude<1e-14f*Mathf.Max(1e-20f,ab.sqrMagnitude*ac.sqrMagnitude))
            {
                float best=float.PositiveInfinity;Vector3 result=a,bary=new Vector3(1,0,0);
                var vertices=new[]{a,b,c};
                for(int i=0;i<3;i++){int j=(i+1)%3;var edge=vertices[j]-vertices[i];float t=edge.sqrMagnitude>1e-20?Mathf.Clamp01(Vector3.Dot(p-vertices[i],edge)/edge.sqrMagnitude):0;var q=vertices[i]+t*edge;float d=(q-p).sqrMagnitude;if(d<best){best=d;result=q;bary=Vector3.zero;bary[i]=1-t;bary[j]=t;}}
                w=bary;return result;
            }
            var ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0){w=new Vector3(1,0,0);return a;}
            var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3){w=new Vector3(0,1,0);return b;}
            float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0){float v=d1/(d1-d3);w=new Vector3(1-v,v,0);return a+v*ab;}
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6){w=new Vector3(0,0,1);return c;}
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0){float v=d2/(d2-d6);w=new Vector3(1-v,0,v);return a+v*ac;}
            float va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0){float v=(d4-d3)/((d4-d3)+(d5-d6));w=new Vector3(0,1-v,v);return b+v*(c-b);}
            float sum=va+vb+vc;if(Math.Abs(sum)<1e-20){w=new Vector3(1,0,0);return a;}
            float y=vb/sum,z=vc/sum;w=new Vector3(1-y-z,y,z);return a+y*ab+z*ac;
        }
    }
}
