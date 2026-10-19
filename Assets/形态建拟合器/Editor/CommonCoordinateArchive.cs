using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VrcModelCard
{
    public class CommonCoordinateArchive:ScriptableObject
    {
        public int schema=1;
        public string sourceKind; // Empty is a legacy model archive.
        public PhotoFitRecord photo;
        public string createdUtc,description,rendererPath,profileJson;
        public CommonCoordinateExport report;
        public Texture2D before,reference,after;
    }

    public static class CommonCoordinateResultActions
    {
        public static SkinnedMeshRenderer Resolve(GameObject root,CommonCoordinateProfile profile,string path,CommonCoordinateFitResult fit)
        {
            if(root==null||!root.scene.IsValid()||EditorUtility.IsPersistent(root))throw new InvalidOperationException("请选择场景中的模型根对象；不会直接修改模型资产。");
            if(profile==null||fit==null||fit.keys==null||fit.weights==null||fit.keys.Length==0||fit.keys.Length!=fit.weights.Length||fit.keys.Distinct().Count()!=fit.keys.Length
                ||fit.weights.Any(w=>!CommonCoordinateDefinitions.Finite(w)||w<0||w>100))throw new InvalidOperationException("存档权重无效。");
            string hash;CommonCoordinateDefinitions.Load(out hash);profile.Validate(hash);
            if(fit.definitionSha256!=hash||!fit.keys.SequenceEqual(profile.keys))throw new InvalidOperationException("结果与坐标配置不一致。");
            var candidates=root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>AnimationUtility.CalculateTransformPath(r.transform,root.transform)==path&&profile.Matches(r.sharedMesh)).ToArray();
            if(candidates.Length!=1)throw new InvalidOperationException("模型路径或网格指纹不匹配；请选择保存结果时对应的模型根对象。");
            var renderer=candidates[0];if(fit.keys.Any(k=>renderer.sharedMesh.GetBlendShapeIndex(k)<0))throw new InvalidOperationException("模型缺少结果中的形态键。");
            if(fit.attempts==null||fit.selectedStart<0||fit.selectedStart>=fit.attempts.Length||fit.attempts[fit.selectedStart].cancelled||!fit.attempts[fit.selectedStart].converged)
                throw new InvalidOperationException("该结果未收敛或已取消，仅允许查看和保存诊断，不允许应用或导出动画。");
            return renderer;
        }
        public static void Apply(GameObject root,CommonCoordinateProfile profile,string path,CommonCoordinateFitResult fit)
        {
            var renderer=Resolve(root,profile,path,fit);
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("应用模型拟合结果");Undo.RecordObject(renderer,"应用模型拟合结果");
            for(int k=0;k<fit.keys.Length;k++)renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(fit.keys[k]),(float)fit.weights[k]);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);EditorSceneManager.MarkSceneDirty(root.scene);Undo.CollapseUndoOperations(group);SceneView.RepaintAll();
        }
        public static AnimationClip MakeClip(GameObject root,CommonCoordinateProfile profile,string rendererPath,CommonCoordinateFitResult fit,bool transition,float duration)
        {
            Resolve(root,profile,rendererPath,fit);
            if(!CommonCoordinateDefinitions.Finite(duration)||duration<=0||duration>60)throw new InvalidOperationException("动画时长必须大于 0 且不超过 60 秒。");
            if(transition&&(fit.referenceWeights==null||fit.referenceWeights.Length!=fit.keys.Length||fit.referenceWeights.Any(w=>!CommonCoordinateDefinitions.Finite(w)||w<0||w>100)))throw new InvalidOperationException("拟合前权重无效。");
            var clip=new AnimationClip{name="模型拟合结果",frameRate=60};
            for(int k=0;k<fit.keys.Length;k++)
            {
                float end=(float)fit.weights[k],start=transition?(float)fit.referenceWeights[k]:end;
                AnimationUtility.SetEditorCurve(clip,EditorCurveBinding.FloatCurve(rendererPath,typeof(SkinnedMeshRenderer),"blendShape."+fit.keys[k]),AnimationCurve.Linear(0,start,duration,end));
            }
            return clip;
        }
        public static void SaveClip(string path,GameObject root,CommonCoordinateProfile profile,string rendererPath,CommonCoordinateFitResult fit,bool transition,float duration)
        {
            CheckAssetPath(path,".anim");var clip=MakeClip(root,profile,rendererPath,fit,transition,duration);
            try{AssetDatabase.CreateAsset(clip,path);AssetDatabase.SaveAssetIfDirty(clip);}catch{if(!AssetDatabase.Contains(clip))UnityEngine.Object.DestroyImmediate(clip);throw;}
        }
        public static void CheckAssetPath(string path,string extension)
        {
            if(string.IsNullOrEmpty(path)||!path.StartsWith("Assets/",StringComparison.Ordinal)||path.Contains("..")||!path.EndsWith(extension,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请保存到 Assets 内的 "+extension+" 文件。");
            if(File.Exists(path)||AssetDatabase.LoadMainAssetAtPath(path)!=null)throw new InvalidOperationException("目标文件已存在，请换一个名字以保留旧存档。");
        }
        public static CommonCoordinateArchive SaveArchive(string path,CommonCoordinateProfile profile,string rendererPath,CommonCoordinateExport report,Texture2D before,Texture2D reference,Texture2D after)
        {
            CheckAssetPath(path,".asset");if(profile==null||report==null||report.fit==null||before==null||after==null)throw new InvalidOperationException("没有完整的拟合预览可保存。");
            var archive=ScriptableObject.CreateInstance<CommonCoordinateArchive>();archive.createdUtc=DateTime.UtcNow.ToString("o");archive.description=report.fit.destinationProfile+" ← "+report.fit.sourceProfile;
            archive.rendererPath=rendererPath;archive.profileJson=JsonUtility.ToJson(profile);archive.report=JsonUtility.FromJson<CommonCoordinateExport>(JsonUtility.ToJson(report));
            AssetDatabase.CreateAsset(archive,path);
            Func<Texture2D,string,Texture2D> save=(source,name)=>{if(source==null)return null;var copy=UnityEngine.Object.Instantiate(source);copy.name=name;copy.hideFlags=HideFlags.HideInHierarchy;AssetDatabase.AddObjectToAsset(copy,archive);return copy;};
            archive.before=save(before,"拟合前");archive.reference=save(reference,"参考长相");archive.after=save(after,"拟合后");
            EditorUtility.SetDirty(archive);AssetDatabase.SaveAssetIfDirty(archive);return archive;
        }
    }

    public class CommonCoordinateArchiveWindow:EditorWindow
    {
        [SerializeField] private CommonCoordinateArchive archive;
        [SerializeField] private GameObject destination;
        private Vector2 scroll,listScroll;
        private string error="";
        private bool transition,details;
        private float duration=1;
        private CommonCoordinateArchive[] library=new CommonCoordinateArchive[0];
        [MenuItem("形态键拟合/拟合存档",false,40)]
        public static void Open(){var w=GetWindow<CommonCoordinateArchiveWindow>("拟合存档");w.minSize=new Vector2(760,600);w.Show();}
        public static void ShowArchive(CommonCoordinateArchive value){Open();GetWindow<CommonCoordinateArchiveWindow>().archive=value;}
        [OnOpenAsset(1)] public static bool OpenAsset(int id,int line){var a=EditorUtility.InstanceIDToObject(id) as CommonCoordinateArchive;if(a==null)return false;ShowArchive(a);return true;}
        private void OnEnable(){Refresh();}
        private void OnProjectChange(){Refresh();Repaint();}
        private void Refresh(){library=AssetDatabase.FindAssets("t:CommonCoordinateArchive").Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<CommonCoordinateArchive>).Where(a=>a!=null).ToArray();}
        private void OnGUI()
        {
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("拟合存档",EditorStyles.largeLabel);
            EditorGUILayout.LabelField("保存的预览可直接查看；选择匹配的场景模型后可应用或导出动画。",EditorStyles.wordWrappedMiniLabel);
            archive=(CommonCoordinateArchive)EditorGUILayout.ObjectField("当前存档",archive,typeof(CommonCoordinateArchive),false);
            if(GUILayout.Button("刷新存档列表"))Refresh();
            listScroll=EditorGUILayout.BeginScrollView(listScroll,GUILayout.Height(Mathf.Min(110,Mathf.Max(30,library.Length*25))));
            foreach(var item in library)if(GUILayout.Button(item.name+" · "+item.description)) {archive=item;error="";}
            if(library.Length==0)EditorGUILayout.LabelField("尚无存档。完成图片或模型拟合后点击“保存拟合存档”。");
            EditorGUILayout.EndScrollView();
            if(archive!=null)
            {
                if(archive.sourceKind=="photo"){DrawPhotoArchive();if(error.Length>0)EditorGUILayout.HelpBox(error,MessageType.Info);EditorGUILayout.EndScrollView();return;}
                if(archive.schema!=1||archive.report==null||archive.report.fit==null){EditorGUILayout.HelpBox("存档版本或内容无效。",MessageType.Error);EditorGUILayout.EndScrollView();return;}
                var fit=archive.report.fit;
                EditorGUILayout.LabelField(archive.description,EditorStyles.boldLabel);
                var textures=new[]{archive.before,archive.reference,archive.after};var labels=new[]{"拟合前","参考长相","拟合后"};float width=Mathf.Max(100,(position.width-50)/3);
                EditorGUILayout.BeginHorizontal();for(int i=0;i<3;i++){EditorGUILayout.BeginVertical(GUILayout.Width(width));EditorGUILayout.LabelField(labels[i]);var r=GUILayoutUtility.GetRect(width,width*760/640);if(textures[i]!=null)GUI.DrawTexture(r,textures[i],ScaleMode.ScaleToFit);else GUI.Label(r,"无参考渲染");EditorGUILayout.EndVertical();}EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField("保存时共同特征误差",fit.featureRmse.ToString("F3")+" 脸宽百分点");
                EditorGUILayout.LabelField("以上为保存时的静态预览；只写入本次候选形态键，其他权重和骨骼保留目标当前状态。",EditorStyles.wordWrappedMiniLabel);
                destination=(GameObject)EditorGUILayout.ObjectField("应用到模型",destination,typeof(GameObject),true);
                using(new EditorGUI.DisabledScope(destination==null))
                {
                    if(GUILayout.Button("应用存档权重（可撤销）",GUILayout.Height(30)))Try(()=>CommonCoordinateResultActions.Apply(destination,Profile(),archive.rendererPath,fit));
                    transition=EditorGUILayout.Toggle("从拟合前过渡到拟合后",transition);duration=EditorGUILayout.FloatField("动画时长（秒）",duration);
                    if(GUILayout.Button("保存为动画…"))Try(()=>{var path=EditorUtility.SaveFilePanelInProject("保存拟合动画","模型拟合结果","anim","绑定路径相对于所选模型根对象。");if(path.Length>0)CommonCoordinateResultActions.SaveClip(path,destination,Profile(),archive.rendererPath,fit,transition,duration);});
                }
                details=EditorGUILayout.Foldout(details,"存档详情",true);
                if(details){EditorGUILayout.LabelField("保存时间（UTC）",archive.createdUtc);EditorGUILayout.LabelField("网格相对路径",archive.rendererPath);for(int i=0;i<fit.keys.Length;i++)EditorGUILayout.LabelField(fit.keys[i],fit.weights[i].ToString("F2"));EditorGUILayout.HelpBox("这是指定网格、语义点配置下的拟合结果。不同模型无法直接互用原生权重，需重新进行共同坐标拟合。",MessageType.Info);}
            }
            if(error.Length>0)EditorGUILayout.HelpBox(error,MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
        private void DrawPhotoArchive()
        {
            var record=archive.photo;
            if(archive.schema!=1||record==null||record.schema!=1||record.weights==null){EditorGUILayout.HelpBox("图片存档内容无效。",MessageType.Error);return;}
            EditorGUILayout.LabelField(archive.description,EditorStyles.boldLabel);
            var textures=new[]{archive.before,archive.reference,archive.after};var labels=new[]{"拟合前","参考图片","拟合后"};float width=Mathf.Max(100,(position.width-50)/3);
            EditorGUILayout.BeginHorizontal();for(int i=0;i<3;i++){EditorGUILayout.BeginVertical(GUILayout.Width(width));EditorGUILayout.LabelField(labels[i]);var r=GUILayoutUtility.GetRect(width,Mathf.Min(320,width*1.25f));if(textures[i]!=null)GUI.DrawTexture(r,textures[i],ScaleMode.ScaleToFit);EditorGUILayout.EndVertical();}EditorGUILayout.EndHorizontal();
            if(!string.IsNullOrEmpty(record.objectiveVersion))
            {
                EditorGUILayout.LabelField("部位均衡二维偏差",string.Format("{0:F2} → {1:F2} 原图像素",record.initialBalancedRmsePixels,record.balancedRmsePixels));
                EditorGUILayout.LabelField("优化代价",string.Format("二维 {0:F3} + 权重先验 {1:F3}",record.dataLoss,record.priorLoss));
            }
            else EditorGUILayout.LabelField("图片点误差",string.Format("{0:F2} → {1:F2} 像素",record.initialRmse*record.originalPixelScale,record.finalRmse*record.originalPixelScale));
            if(record.cancelled)EditorGUILayout.HelpBox("这是取消拟合后的诊断存档，不允许应用或导出动画。",MessageType.Warning);
            EditorGUILayout.LabelField("静态预览；只应用本次候选键（包括零权重），其余键保留目标模型当前状态。",EditorStyles.wordWrappedMiniLabel);
            destination=(GameObject)EditorGUILayout.ObjectField("应用到模型",destination,typeof(GameObject),true);
            using(new EditorGUI.DisabledScope(destination==null||record.cancelled))
            {
                if(GUILayout.Button("应用存档权重（可撤销）",GUILayout.Height(30)))Try(()=>PhotoFitResultActions.Apply(destination,record));
                transition=EditorGUILayout.Toggle("从拟合前过渡到拟合后",transition);duration=EditorGUILayout.FloatField("动画时长（秒）",duration);
                if(GUILayout.Button("保存为动画…"))Try(()=>{var p=EditorUtility.SaveFilePanelInProject("保存图片拟合动画","图片拟合结果","anim","绑定路径相对于所选模型根对象。");if(p.Length>0)PhotoFitResultActions.SaveClip(p,destination,record,transition,duration);});
            }
            details=EditorGUILayout.Foldout(details,"存档详情",true);
            if(details)
            {
                EditorGUILayout.LabelField("保存时间（UTC）",archive.createdUtc);EditorGUILayout.LabelField("网格相对路径",record.rendererPath);
                EditorGUILayout.LabelField("初值数 / 所选初值",(record.attempts==null?0:record.attempts.Length)+" / "+(record.selectedStart+1));
                foreach(var w in record.weights)EditorGUILayout.LabelField(w.name,w.before.ToString("F2")+" → "+w.after.ToString("F2")+(w.atBoundary?" [触边]":""));
                EditorGUILayout.HelpBox(record.limitation,MessageType.Info);
            }
        }
        private CommonCoordinateProfile Profile(){return JsonUtility.FromJson<CommonCoordinateProfile>(archive.profileJson);}
        private void Try(Action action){try{error="";action();error="操作完成。";}catch(Exception ex){error=ex.Message;}}
    }
}
