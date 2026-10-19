using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrcModelCard
{
    // Sample only a disposable renderer. Supports Unity skinning without assumptions
    // about bone layout, mesh orientation or manufacturer shape names.
    public static class CommonCoordinateCalibration
    {
        public static CommonCoordinateModel Capture(CommonCoordinateProfile profile,SkinnedMeshRenderer original)
        {
            string hash;CommonCoordinateDefinitions.Load(out hash);profile.Validate(hash);
            if(original==null || !profile.Matches(original.sharedMesh))throw new InvalidOperationException("配置与网格不匹配。");
            var mesh=original.sharedMesh;
            var indices=profile.keys.Select(mesh.GetBlendShapeIndex).ToArray();
            if(indices.Any(i=>i<0))throw new InvalidOperationException("候选形态键不存在。");
            foreach(int k in indices)
                if(mesh.GetBlendShapeFrameCount(k)!=1 || mesh.GetBlendShapeFrameWeight(k,0)<=0)
                    throw new InvalidOperationException("线性坐标拟合需要单个正权重帧；请取消选择多帧键："+mesh.GetBlendShapeName(k));
            var reference=indices.Select(k=>(double)original.GetBlendShapeWeight(k)).ToArray();
            if(reference.Any(w=>!CommonCoordinateDefinitions.Finite(w)||w<0||w>100))throw new InvalidOperationException("候选键当前权重必须在 0–100 范围内。");
            var scene=EditorSceneManager.NewPreviewScene();var baked=new Mesh();
            try
            {
                var go=new GameObject("__CoordinateCalibration");SceneManager.MoveGameObjectToScene(go,scene);
                go.transform.SetPositionAndRotation(original.transform.position,original.transform.rotation);go.transform.localScale=original.transform.lossyScale;
                var copy=go.AddComponent<SkinnedMeshRenderer>();EditorUtility.CopySerialized(original,copy);copy.enabled=false;
                Func<Vector3[]> bake=()=>{copy.BakeMesh(baked);return baked.vertices;};
                original.BakeMesh(baked);var originalVertices=baked.vertices;
                if(originalVertices.Any(v=>!CommonCoordinateDefinitions.Finite(v.x)||!CommonCoordinateDefinitions.Finite(v.y)||!CommonCoordinateDefinitions.Finite(v.z)))throw new InvalidOperationException("网格或当前形态权重含非有限数值。");
                var current=bake();double error=Enumerable.Range(0,current.Length).Max(i=>(double)(current[i]-originalVertices[i]).magnitude);
                if(error>5e-5)throw new InvalidOperationException("独立蒙皮采样与原网格不一致："+error);
                var all=Enumerable.Range(0,mesh.blendShapeCount).Select(original.GetBlendShapeWeight).ToArray();
                for(int i=0;i<all.Length;i++)copy.SetBlendShapeWeight(i,0);
                var neutral=bake();
                Func<string,Vector3> point=id=>Point(neutral,profile.points.First(p=>p.id==id));
                var left=point("left_face_cheek");var right=point("right_face_cheek");var chin=point("chin");
                var x=(right-left).normalized;float scale=(right-left).magnitude;
                var eye=(point("left_eye_inner")+point("right_eye_inner"))*.5f;
                var up=eye-chin;var y=(up-x*Vector3.Dot(up,x)).normalized;var z=Vector3.Cross(x,y).normalized;
                if(scale<1e-6 || y.sqrMagnitude<.9f || z.sqrMagnitude<.9f)throw new InvalidOperationException("脸颊、下巴与眼部基准退化，请重新标记。");
                // Internal model convention is flipped X/Z; public anchors are X right, Y up.
                Func<Vector3,Vector3> vector=v=>new Vector3(-Vector3.Dot(v,x),Vector3.Dot(v,y),-Vector3.Dot(v,z))/scale;
                var origin=(left+right)*.5f;origin+=y*Vector3.Dot(chin-origin,y);origin+=z*Vector3.Dot(point("nose_tip")-origin,z);
                var model=new CommonCoordinateModel{profile=profile,renderer=original,indices=indices,reference=reference,scale=1,center=Vector3.zero,bakeMaxError=error,
                    currentVertices=current.Select(v=>vector(v-origin)).ToArray(),deltas=new Vector3[indices.Length][],anchorDeltas=new double[indices.Length,profile.points.Length,3]};
                for(int i=0;i<all.Length;i++)copy.SetBlendShapeWeight(i,all[i]);
                model.anchors=model.Anchors(model.currentVertices);
                for(int k=0;k<indices.Length;k++)
                {
                    int index=indices[k];copy.SetBlendShapeWeight(index,0);var zero=bake();copy.SetBlendShapeWeight(index,100);var full=bake();
                    copy.SetBlendShapeWeight(index,all[index]);
                    model.deltas[k]=Enumerable.Range(0,zero.Length).Select(v=>vector(full[v]-zero[v])/100).ToArray();
                    for(int p=0;p<profile.points.Length;p++)
                    {
                        var d=Point(model.deltas[k],profile.points[p]);model.anchorDeltas[k,p,0]=-d.x;model.anchorDeltas[k,p,1]=d.y;model.anchorDeltas[k,p,2]=-d.z;
                    }
                }
                // A mixed-weight probe verifies linear superposition on the actual Unity mesh.
                var probe=reference.Select((w,i)=>17.0+(i*29)%67).ToArray();
                for(int k=0;k<indices.Length;k++)copy.SetBlendShapeWeight(indices[k],(float)probe[k]);
                var actual=bake().Select(v=>vector(v-origin)).ToArray();var predicted=model.MeshVertices(probe);
                var gap=Enumerable.Range(0,actual.Length).Max(v=>(actual[v]-predicted[v]).magnitude);
                if(gap>2e-4)throw new InvalidOperationException("形态响应非线性或采样误差过大："+gap+" 脸宽单位。");
                model.bakeMaxError=Math.Max(error,gap*scale);return model;
            }
            finally{UnityEngine.Object.DestroyImmediate(baked);EditorSceneManager.ClosePreviewScene(scene);}
        }
        public static Vector3 Point(Vector3[] vertices,PhotoLandmarkDefinition p)
        {var v=Vector3.zero;for(int i=0;i<p.vertices.Length;i++)v+=vertices[p.vertices[i]]*p.weights[i];return v;}
        public static bool Pick(SkinnedMeshRenderer renderer,Ray worldRay,out int[] vertices,out float[] weights)
        {
            vertices=null;weights=null;var baked=new Mesh();float best=float.PositiveInfinity;
            try
            {
                renderer.BakeMesh(baked);var v=baked.vertices;var tris=baked.triangles;
                var ray=new Ray(renderer.transform.InverseTransformPoint(worldRay.origin),renderer.transform.InverseTransformVector(worldRay.direction));
                for(int i=0;i<tris.Length;i+=3)
                {
                    var a=v[tris[i]];var e1=v[tris[i+1]]-a;var e2=v[tris[i+2]]-a;var p=Vector3.Cross(ray.direction,e2);float det=Vector3.Dot(e1,p);
                    if(Math.Abs(det)<1e-12)continue;var t=ray.origin-a;float u=Vector3.Dot(t,p)/det;if(u<0||u>1)continue;
                    var q=Vector3.Cross(t,e1);float b=Vector3.Dot(ray.direction,q)/det;if(b<0||u+b>1)continue;
                    float distance=Vector3.Dot(e2,q)/det;if(distance<=0||distance>=best)continue;
                    best=distance;vertices=new[]{tris[i],tris[i+1],tris[i+2]};weights=new[]{1-u-b,u,b};
                }
                return vertices!=null;
            }
            finally{UnityEngine.Object.DestroyImmediate(baked);}
        }
    }
}
