using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class PhotoFitAttempt
    {
        public float loss,rmse;
        public float dataLoss,priorLoss,balancedRmsePixels;
        public float[] initialWeights,finalWeights;
    }
    [Serializable] public class PhotoFitRecord
    {
        public int schema=1,seed,selectedStart,evaluations;
        public string modelName,rendererPath,profileJson,annotationJson,imageSha256,settingsJson,unityVersion;
        public float initialRmse,finalRmse,originalPixelScale,imageRollDegrees,loss;
        public string objectiveVersion;
        public float dataLoss,priorLoss,balancedRmsePixels,initialBalancedRmsePixels;
        public Vector2[] contourModelSamples,contourImageSamples;
        public float[] contourSampleWeights;
        public bool cancelled;
        public FitWeightChange[] weights;
        public PhotoFitAttempt[] attempts;
        public ShapeCandidate[] candidates;
        public string[] landmarkNames;
        public Vector2[] fittedLandmarks;
        public string limitation="指定模型与语义映射下的正面二维拟合；深度、转头、发型和材质需人工判断。多初值搜索不证明全局最优。";
    }
    public static class PhotoFitResultActions
    {
        public static PhotoFitRecord Capture(GameObject root,SkinnedMeshRenderer renderer,CommonCoordinateProfile profile,PhotoAnnotation annotation,FitSettings settings,FitResult fit,float pixelScale)
        {
            var result=new PhotoFitRecord{modelName=root.name,rendererPath=AnimationUtility.CalculateTransformPath(renderer.transform,root.transform),
                profileJson=JsonUtility.ToJson(profile),annotationJson=JsonUtility.ToJson(annotation),imageSha256=annotation.imageSha256,
                objectiveVersion=fit.objectiveVersion,dataLoss=fit.dataLoss,priorLoss=fit.priorLoss,balancedRmsePixels=fit.balancedRmsePixels,initialBalancedRmsePixels=fit.initialBalancedRmsePixels,
                contourModelSamples=fit.contourModelSamples,contourImageSamples=fit.contourImageSamples,
                contourSampleWeights=fit.contourSampleWeights,
                settingsJson=JsonUtility.ToJson(settings),unityVersion=Application.unityVersion,seed=settings.seed,selectedStart=fit.selectedStart,evaluations=fit.renders,
                initialRmse=fit.initialLandmarkRmse,finalRmse=fit.finalLandmarkRmse,originalPixelScale=pixelScale,imageRollDegrees=fit.imageRollDegrees,loss=fit.loss,
                cancelled=fit.cancelled,weights=fit.weightChanges.ToArray(),candidates=fit.candidates.ToArray(),landmarkNames=fit.landmarkNames,fittedLandmarks=fit.fittedLandmarks,
                attempts=Enumerable.Range(0,fit.startLosses.Count).Select(i=>new PhotoFitAttempt{loss=fit.startLosses[i],rmse=fit.startRmses[i],initialWeights=fit.startInitialWeights[i],finalWeights=fit.startFinalWeights[i],
                    dataLoss=i<fit.startDataLosses.Count?fit.startDataLosses[i]:0,priorLoss=i<fit.startPriorLosses.Count?fit.startPriorLosses[i]:0,balancedRmsePixels=i<fit.startBalancedRmses.Count?fit.startBalancedRmses[i]:0}).ToArray()};
            return JsonUtility.FromJson<PhotoFitRecord>(JsonUtility.ToJson(result));
        }
        public static SkinnedMeshRenderer Resolve(GameObject root,PhotoFitRecord record)
        {
            if(root==null||!root.scene.IsValid()||EditorUtility.IsPersistent(root))throw new InvalidOperationException("请选择匹配的场景模型根对象。");
            if(record==null||record.schema!=1||record.weights==null||record.weights.Length==0||record.weights.Any(w=>w==null||string.IsNullOrEmpty(w.name)||w.rendererPath!=record.rendererPath||!ValidWeight(w.after)||!ValidWeight(w.before))||record.weights.Select(w=>w.name).Distinct().Count()!=record.weights.Length)
                throw new InvalidOperationException("图片拟合权重或路径无效。");
            if(record.cancelled||!CommonCoordinateDefinitions.Finite(record.loss)||!CommonCoordinateDefinitions.Finite(record.finalRmse)||record.attempts==null||record.selectedStart<0||record.selectedStart>=record.attempts.Length)
                throw new InvalidOperationException("拟合已取消或未产生完整结果；只可保存诊断存档。");
            var selected=record.attempts[record.selectedStart];
            if(selected==null||!CommonCoordinateDefinitions.Finite(selected.loss)||selected.finalWeights==null||selected.finalWeights.Length!=record.weights.Length||selected.finalWeights.Where((w,i)=>!ValidWeight(w)||Mathf.Abs(w-record.weights[i].after)>.001f).Any())
                throw new InvalidOperationException("所选初值与最终权重不一致。");
            var profile=JsonUtility.FromJson<CommonCoordinateProfile>(record.profileJson);string hash;CommonCoordinateDefinitions.Load(out hash);profile.Validate(hash);
            if(record.weights.Any(w=>!profile.keys.Contains(w.name)))throw new InvalidOperationException("权重不属于保存的模型坐标。");
            var matches=root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>AnimationUtility.CalculateTransformPath(r.transform,root.transform)==record.rendererPath&&profile.Matches(r.sharedMesh)).ToArray();
            if(matches.Length!=1||record.weights.Any(w=>matches[0].sharedMesh.GetBlendShapeIndex(w.name)<0))throw new InvalidOperationException("模型路径或网格指纹不匹配。跨模型请重新拟合。");
            return matches[0];
        }
        private static bool ValidWeight(float w){return CommonCoordinateDefinitions.Finite(w)&&w>=0&&w<=100;}
        public static void Apply(GameObject root,PhotoFitRecord record)
        {
            var r=Resolve(root,record);Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("应用图片拟合结果");Undo.RecordObject(r,"应用图片拟合结果");
            foreach(var w in record.weights)r.SetBlendShapeWeight(r.sharedMesh.GetBlendShapeIndex(w.name),w.after);
            PrefabUtility.RecordPrefabInstancePropertyModifications(r);EditorSceneManager.MarkSceneDirty(root.scene);Undo.CollapseUndoOperations(group);SceneView.RepaintAll();
        }
        public static AnimationClip MakeClip(GameObject root,PhotoFitRecord record,bool transition,float duration)
        {
            Resolve(root,record);if(!CommonCoordinateDefinitions.Finite(duration)||duration<=0||duration>60)throw new InvalidOperationException("动画时长必须大于 0 且不超过 60 秒。");
            var clip=new AnimationClip{name=record.modelName+"_图片拟合",frameRate=60};
            foreach(var w in record.weights)AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(record.rendererPath,typeof(SkinnedMeshRenderer),"blendShape."+w.name),AnimationCurve.Linear(0,transition?w.before:w.after,duration,w.after));
            return clip;
        }
        public static void SaveClip(string path,GameObject root,PhotoFitRecord record,bool transition,float duration)
        {
            CommonCoordinateResultActions.CheckAssetPath(path,".anim");var clip=MakeClip(root,record,transition,duration);
            try{AssetDatabase.CreateAsset(clip,path);AssetDatabase.SaveAssetIfDirty(clip);}catch{if(!AssetDatabase.Contains(clip))UnityEngine.Object.DestroyImmediate(clip);throw;}
        }
        public static CommonCoordinateArchive SaveArchive(string path,PhotoFitRecord record,Texture2D before,Texture2D reference,Texture2D after)
        {
            CommonCoordinateResultActions.CheckAssetPath(path,".asset");if(record==null||before==null||reference==null||after==null)throw new InvalidOperationException("缺少结果或预览，无法保存存档。");
            var archive=ScriptableObject.CreateInstance<CommonCoordinateArchive>();archive.sourceKind="photo";archive.createdUtc=DateTime.UtcNow.ToString("o");archive.description=record.modelName+" ← 图片拟合";
            archive.photo=JsonUtility.FromJson<PhotoFitRecord>(JsonUtility.ToJson(record));archive.rendererPath=record.rendererPath;archive.profileJson=record.profileJson;
            AssetDatabase.CreateAsset(archive,path);
            Func<Texture2D,string,Texture2D> embed=(source,name)=>{var t=UnityEngine.Object.Instantiate(source);t.name=name;t.hideFlags=HideFlags.HideInHierarchy;AssetDatabase.AddObjectToAsset(t,archive);return t;};
            archive.before=embed(before,"拟合前");archive.reference=embed(reference,"参考图片");archive.after=embed(after,"拟合后");EditorUtility.SetDirty(archive);AssetDatabase.SaveAssetIfDirty(archive);return archive;
        }
    }
    public partial class ModelCardFitWindow
    {
        private PhotoFitRecord photoFitRecord;
        private bool photoAnimationTransition;
        private float photoAnimationDuration=1;
        private string photoActionStatus;
        private void PhotoResultAction(Action action){try{action();photoActionStatus="操作完成。";}catch(Exception ex){photoActionStatus=ex.Message;}}
        public void ApplyPhotoResult(){if(root!=fittedRoot)throw new InvalidOperationException("模型已改变，请重新拟合。");PhotoFitResultActions.Apply(root,photoFitRecord);}
        public void SavePhotoAnimation(string path,bool transition,float duration){if(root!=fittedRoot)throw new InvalidOperationException("模型已改变，请重新拟合。");PhotoFitResultActions.SaveClip(path,root,photoFitRecord,transition,duration);}
        public CommonCoordinateArchive SavePhotoArchive(string path){return PhotoFitResultActions.SaveArchive(path,photoFitRecord,fitBeforePreview,imageTex,fitAfterPreview);}
        private void DrawPhotoResultActions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            using(new EditorGUI.DisabledScope(root==null||root!=fittedRoot||photoFitRecord.cancelled))
            {
                if(GUILayout.Button("应用到模型（可撤销）",GUILayout.Height(32)))PhotoResultAction(ApplyPhotoResult);
                if(GUILayout.Button("保存为动画…",GUILayout.Height(32)))PhotoResultAction(()=>{var p=EditorUtility.SaveFilePanelInProject("保存图片拟合动画",PhotoResultFileName(),"anim","保存固定姿态或拟合前后过渡。");if(p.Length>0)SavePhotoAnimation(p,photoAnimationTransition,photoAnimationDuration);});
            }
            if(GUILayout.Button("保存拟合存档…",GUILayout.Height(32)))PhotoResultAction(()=>{var p=EditorUtility.SaveFilePanelInProject("保存图片拟合存档",PhotoResultFileName(),"asset","包含参考图、前后预览、完整权重和标注。");if(p.Length>0)CommonCoordinateArchiveWindow.ShowArchive(SavePhotoArchive(p));});
            EditorGUILayout.EndHorizontal();
            photoAnimationTransition=EditorGUILayout.Toggle("动画：从拟合前过渡到拟合后",photoAnimationTransition);
            if(photoAnimationTransition)photoAnimationDuration=EditorGUILayout.Slider("过渡时长（秒）",photoAnimationDuration,.1f,10);
            EditorGUILayout.LabelField("存档可在“形态键拟合 / 拟合存档”预览、再次应用或导出动画；未勾选过渡时导出固定姿态。",EditorStyles.wordWrappedMiniLabel);
            if(!string.IsNullOrEmpty(photoActionStatus))EditorGUILayout.HelpBox(photoActionStatus,MessageType.Info);
            EditorGUILayout.EndVertical();
        }
        private string PhotoResultFileName(){return string.Concat(photoFitRecord.modelName.Select(c=>Array.IndexOf(System.IO.Path.GetInvalidFileNameChars(),c)>=0?'_':c))+"_图片拟合_"+DateTime.Now.ToString("yyyyMMdd_HHmmss");}
    }
}
