using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrcModelCard
{
    // Registered semantic anchors, independent of avatar and BlendShape names.
    public static class GenericPhotoModel
    {
        public static PhotoLandmarkTemplate Template(CommonCoordinateProfile profile)
        {
            string hash; CommonCoordinateDefinitions.Load(out hash); profile.Validate(hash);
            return PhotoDetailLandmarks.Expand(new PhotoLandmarkTemplate {
                schema=1, id=PhotoLandmarkTemplate.SemanticId, description=profile.description,
                vertexCount=profile.vertexCount, blendShapeCount=profile.blendShapeCount,
                topologySha256=profile.topologySha256, shapeNamesSha256=profile.shapeNamesSha256,
                points=profile.points
            });
        }

        public static CommonCoordinateProfile Bind(GameObject root, CommonCoordinateProfile selected, out SkinnedMeshRenderer renderer)
        {
            string hash; CommonCoordinateDefinitions.Load(out hash);
            var profile=CommonCoordinateProfile.Bind(root,hash,out renderer,selected);
            foreach (var key in profile.keys)
            {
                int k=renderer.sharedMesh.GetBlendShapeIndex(key);
                if (k<0) throw new InvalidOperationException("坐标档案的形态键已不存在："+key);
                if (renderer.sharedMesh.GetBlendShapeFrameCount(k)!=1 || renderer.sharedMesh.GetBlendShapeFrameWeight(k,0)<=0)
                    throw new InvalidOperationException("当前坐标原型仅支持单个正权重帧，请在构建坐标中取消多帧键："+key);
                float value=renderer.GetBlendShapeWeight(k);
                if (!CommonCoordinateDefinitions.Finite(value) || value<0 || value>100)
                    throw new InvalidOperationException("候选键当前权重须在 0–100 内："+key);
            }
            return profile;
        }

        // Remove the displacement explained by image translation, uniform scale
        // and optionally roll. Selection never uses target weights or key names.
        public static float ObservableInfluence(Vector2[] reference, Vector2[] zero, Vector2[] full, bool fitRoll, float probe)
        {
            if(reference.Length<4 || zero.Length!=reference.Length || full.Length!=reference.Length)
                throw new InvalidOperationException("需要至少四个可见语义点。");
            Vector2 center=Vector2.zero, mean=Vector2.zero;
            for(int i=0;i<reference.Length;i++){center+=reference[i];mean+=full[i]-zero[i];}
            center/=reference.Length;mean/=reference.Length;
            double denominator=0,scale=0,roll=0;
            for(int i=0;i<reference.Length;i++)
            {
                var p=reference[i]-center;var d=full[i]-zero[i]-mean;
                denominator+=p.sqrMagnitude;scale+=Vector2.Dot(p,d);roll+=p.x*d.y-p.y*d.x;
            }
            if(denominator<1e-8)throw new InvalidOperationException("模型特征点分布退化，请检查坐标档案。");
            scale/=denominator;roll=fitRoll?roll/denominator:0;
            double sum=0;
            for(int i=0;i<reference.Length;i++)
            {
                var p=reference[i]-center;
                var d=full[i]-zero[i]-mean-(float)scale*p-(float)roll*new Vector2(-p.y,p.x);
                sum+=d.sqrMagnitude;
            }
            return (float)Math.Sqrt(sum/(2*reference.Length))*probe/100f;
        }

        public static void Prepare(GameObject root, Texture2D image, LandmarkSet lm, FitSettings settings)
        {
            if(settings.stabilizePhotoFit)lm=PhotoContourObjective.CandidateLandmarks(lm);
            SkinnedMeshRenderer renderer;
            var profile=Bind(root,settings.photoProfile,out renderer);
            settings.photoTemplate=Template(profile);
            if(lm==null || lm.Count<4 || lm.renderAnchors.Any(a=>a.smr!=renderer))
                throw new InvalidOperationException("图片语义点与所选模型不匹配。");
            var observed=lm.renderAnchors.Select(a=>settings.photoTemplate.points.Single(p=>p.id==a.semanticId)).ToArray();
            var size=SilhouetteMask.Extract(image,settings.workWidth,settings.bgTolerance);
            var scene=EditorSceneManager.NewPreviewScene();var baked=new Mesh();
            var audit=new List<ShapeCandidate>();
            try
            {
                var go=new GameObject("__PhotoCandidateProbe");SceneManager.MoveGameObjectToScene(go,scene);
                go.transform.SetPositionAndRotation(renderer.transform.position,renderer.transform.rotation);
                go.transform.localScale=renderer.transform.lossyScale;
                var copy=go.AddComponent<SkinnedMeshRenderer>();EditorUtility.CopySerialized(renderer,copy);copy.enabled=false;
                var camGo=new GameObject("__PhotoCandidateCamera");SceneManager.MoveGameObjectToScene(camGo,scene);
                var camera=camGo.AddComponent<Camera>();camera.enabled=false;
                PhotoLandmarkTemplate.ConfigureCamera(settings.photoTemplate,renderer,camera,size.width,size.height);
                Func<Vector2[]> project=()=>{
                    copy.BakeMesh(baked);var vertices=baked.vertices;
                    return observed.Select(p=>{
                        var v=camera.WorldToViewportPoint(renderer.transform.TransformPoint(CommonCoordinateCalibration.Point(vertices,p)));
                        if(!CommonCoordinateDefinitions.Finite(v.x)||!CommonCoordinateDefinitions.Finite(v.y))
                            throw new InvalidOperationException("模型采样产生非有限坐标。");
                        return new Vector2(v.x*size.width,v.y*size.height);
                    }).ToArray();
                };
                var baseline=project();
                // Express the cutoff in original-photo pixels, not the reduced
                // working buffer; otherwise small but visible eye changes vanish.
                float photoScale=FittingOptimizer.FitNormalizerFromLandmarks(lm,camera,size.width,size.height,settings.fitImageRoll).scale
                    * image.width / size.width;
                float probe=Mathf.Clamp(settings.candidateProbeWeight,5,50);
                foreach(var key in profile.keys)
                {
                    int index=renderer.sharedMesh.GetBlendShapeIndex(key);float original=copy.GetBlendShapeWeight(index);
                    copy.SetBlendShapeWeight(index,0);var zero=project();
                    copy.SetBlendShapeWeight(index,100);var full=project();copy.SetBlendShapeWeight(index,original);
                    float influence=ObservableInfluence(baseline,zero,full,settings.fitImageRoll,probe)*photoScale;
                    bool selected=influence>=.25f;
                    audit.Add(new ShapeCandidate{name=key,rendererPath=ModelCardIO.GetRelativePath(root.transform,renderer.transform),
                        influencePixels=influence,selected=selected,
                        reason=selected?"已标点存在二维形变响应；不代表参数可唯一恢复":"二维响应较弱或可由对齐解释（含深度方向），固定当前权重"});
                }
                settings.photoCandidateAudit=audit.OrderByDescending(a=>a.influencePixels).ToList();
                settings.photoKeys=audit.Where(a=>a.selected).Select(a=>a.name).ToArray();
                if(settings.photoKeys.Length==0)
                    throw new InvalidOperationException("当前已标点未约束坐标档案中的候选键。请增加可见特征点，或检查模型坐标和候选键；未修改模型权重。");
            }
            finally{UnityEngine.Object.DestroyImmediate(baked);EditorSceneManager.ClosePreviewScene(scene);}
        }
    }
}
