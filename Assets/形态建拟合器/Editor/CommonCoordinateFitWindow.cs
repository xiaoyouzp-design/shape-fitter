using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrcModelCard
{
    [Serializable] public class CommonCoordinateValue { public string id; public double value; }
    [Serializable] public class CommonCoordinatePoint { public string id; public double x,y,z; }
    [Serializable] public class CommonCoordinateTarget
    {
        public int schema=1;
        public string definition_sha256,unit="shared_geometry",source;
        public CommonCoordinateValue[] values;
        public CommonCoordinatePoint[] detail_points;
    }
    [Serializable] public class CommonCoordinateSceneAudit
    {
        public bool weightsUnchanged,dirtyUnchanged,selectionUnchanged;
        public int renderersChecked;
    }
    [Serializable] public class CommonCoordinateExport
    {
        public CommonCoordinateFitResult fit;
        public CommonCoordinateTarget target;
        public CommonCoordinateSceneAudit sceneAudit;
        public string limitation="Five starts do not prove global optimality for nonlinear/photo problems. Display ranges are shared engineering units, not universal anatomy defaults.";
    }

    public class CommonCoordinateFitWindow : EditorWindow
    {
        [SerializeField] public GameObject sourceRoot,destinationRoot;
        [SerializeField] public TextAsset sourceProfile,destinationProfile;
        [SerializeField] private bool depth,allDetailPoints=true;
        [SerializeField] private int lossMode=2,priorMode,seed=20261015;
        private CommonCoordinateDefinitions definitions;
        private string definitionHash,error="";
        private CommonCoordinateTarget importedTarget,lastTarget;
        private CommonCoordinateFitResult result;
        private CommonCoordinateSceneAudit sceneAudit;
        private Texture2D beforePreview,targetPreview,afterPreview;
        private Vector2 scroll;
        private bool showCoordinates,showWeights,showAdvanced,showDetails,showExport;
        private CommonCoordinateProfile fittedProfile;
        private GameObject fittedRoot;
        private string fittedRendererPath;
        private bool transition;
        private float animationDuration=1;
        public CommonCoordinateFitResult Result { get { return result; } }
        public CommonCoordinateSceneAudit SceneAudit { get { return sceneAudit; } }

        [MenuItem("形态键拟合/模型拟合", false, 20)]
        public static void Open()
        {
            var window=GetWindow<CommonCoordinateFitWindow>("模型拟合");window.minSize=new Vector2(760,620);window.Show();
        }
        private void OnEnable()
        {
            try{definitions=CommonCoordinateDefinitions.Load(out definitionHash);}catch(Exception ex){error=ex.Message;}
        }
        private void OnDisable(){Clear();}
        private void Clear()
        {
            foreach(var t in new[]{beforePreview,targetPreview,afterPreview})if(t!=null)DestroyImmediate(t);
            beforePreview=targetPreview=afterPreview=null;result=null;lastTarget=null;sceneAudit=null;fittedProfile=null;fittedRoot=null;fittedRendererPath=null;
        }
        private void OnGUI()
        {
            titleContent=new GUIContent("模型拟合");
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("模型拟合",EditorStyles.largeLabel);
            EditorGUILayout.LabelField("选择参考长相和待调整模型。已有坐标配置可直接拟合，新模型先构建坐标。",EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            using(new EditorGUI.DisabledScope(importedTarget!=null))
                sourceRoot=(GameObject)EditorGUILayout.ObjectField("参考模型",sourceRoot,typeof(GameObject),true);
            destinationRoot=(GameObject)EditorGUILayout.ObjectField("待调整模型",destinationRoot,typeof(GameObject),true);
            if(EditorGUI.EndChangeCheck())Clear();
            using(new EditorGUI.DisabledScope(importedTarget!=null))
                if(GUILayout.Button("交换两个模型")){var old=sourceRoot;sourceRoot=destinationRoot;destinationRoot=old;var profile=sourceProfile;sourceProfile=destinationProfile;destinationProfile=profile;Clear();}
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("为新模型构建坐标…"))CommonCoordinateBuilderWindow.Open();
            if(GUILayout.Button("浏览拟合存档…"))CommonCoordinateArchiveWindow.Open();
            EditorGUILayout.EndHorizontal();
            if(importedTarget!=null)
            {
                EditorGUILayout.HelpBox("参考来自已导入的坐标文件。",MessageType.Info);
                if(GUILayout.Button("恢复使用参考模型")){importedTarget=null;Clear();}
            }
            showAdvanced=EditorGUILayout.Foldout(showAdvanced,"高级设置",true);
            if(showAdvanced)
            {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            sourceProfile=(TextAsset)EditorGUILayout.ObjectField("参考坐标配置（可选）",sourceProfile,typeof(TextAsset),false);
            destinationProfile=(TextAsset)EditorGUILayout.ObjectField("目标坐标配置（可选）",destinationProfile,typeof(TextAsset),false);
            depth=EditorGUILayout.Popup("拟合几何",depth?1:0,new[]{"正面二维特征","三维特征（需要模型／三维坐标）"})==1;
            lossMode=EditorGUILayout.Popup("匹配方式",lossMode,new[]{"对应点细节","共同特征坐标","对应点＋共同坐标"});
            allDetailPoints=EditorGUILayout.Toggle("细节使用全部 42 点",allDetailPoints);
            priorMode=EditorGUILayout.Popup("原生权重先验",priorMode,new[]{"弱：0.002","较强：0.02"});
            seed=EditorGUILayout.IntField("随机种子（共 5 初值）",seed);
            if(EditorGUI.EndChangeCheck())Clear();
            if(GUILayout.Button("导入共同坐标目标…"))Import();
            if(importedTarget!=null)EditorGUILayout.HelpBox("坐标来源："+importedTarget.source+(importedTarget.detail_points==null?"；文件不含点细节，将只拟合共同坐标。":""),MessageType.Info);
            EditorGUILayout.LabelField("混合损失：点残差系数 1，共同坐标残差系数 0.25；均采用固定基准脸宽单位。此系数是实验设置，未经独立验证集调优。",EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
            }
            using(new EditorGUI.DisabledScope(definitions==null || destinationRoot==null || (sourceRoot==null && importedTarget==null)))
                if(GUILayout.Button("开始拟合并预览",GUILayout.Height(34)))RunFit();
            EditorGUILayout.LabelField("结果在窗口中预览，不自动应用到场景。",EditorStyles.wordWrappedMiniLabel);
            if(!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Error);
            if(result!=null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("拟合结果",EditorStyles.boldLabel);
                DrawPreviews();
                EditorGUILayout.LabelField("共同特征误差",result.featureRmse.ToString("F3")+" 脸宽百分点");
                int failed=result.attempts.Count(a=>!a.converged);
                if(failed>0)EditorGUILayout.HelpBox(failed+" 个初值未达到收敛阈值，均已保留；可导出报告查看梯度、迭代次数和取消状态。",MessageType.Warning);
                if(result.individuallyUnreachable.Length>0)EditorGUILayout.HelpBox(result.individuallyUnreachable.Length+" 项特征超出当前可达范围，细节见“拟合详情”。",MessageType.Warning);
                if(result.upperBoundary.Length+result.lowerBoundary.Length>0)EditorGUILayout.LabelField("部分形态键已达到允许范围，详见参数权重。",EditorStyles.wordWrappedMiniLabel);
                showDetails=EditorGUILayout.Foldout(showDetails,"拟合详情与限制",true);
                if(showDetails)
                {
                EditorGUILayout.LabelField("待调整模型／参考",result.destinationProfile+" / "+result.sourceProfile);
                EditorGUILayout.LabelField("本次设置",(depth?"三维特征":"正面二维特征")+"；"+new[]{"对应点细节","共同特征坐标","对应点＋共同坐标"}[lossMode]);
                EditorGUILayout.LabelField("先验残差平方和",result.priorSse.ToString("G5"));
                if(result.hasTargetPoints)EditorGUILayout.LabelField("对应点误差","正面 "+result.frontalPointRmse.ToString("F4")+"%；三维 "+result.geometryPointRmse.ToString("F4")+"%（固定基准脸宽）");
                EditorGUILayout.LabelField("求解记录","上界 "+result.upperBoundary.Length+" 键；下界 "+result.lowerBoundary.Length+" 键；初值 "+(result.selectedStart+1)+"/"+result.attempts.Length+"；目标 "+result.objective.ToString("G7"));
                if(result.individuallyUnreachable.Length>0)EditorGUILayout.LabelField("超出独立可达范围："+string.Join(", ",result.individuallyUnreachable)+"。其余特征也不保证同时达到。",EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.HelpBox("二维模式的深度没有被观测；相关深度键主要受先验与形态耦合影响。三维模式也不保证唯一原生权重。材质、虹膜与头发不会随几何拟合互换。5 个初值不能证明任意非线性问题全局最优。",MessageType.Info);
                }
                showCoordinates=EditorGUILayout.Foldout(showCoordinates,"共同坐标：目标／拟合／误差",true);
                if(showCoordinates)for(int i=0;i<definitions.definitions.Length;i++)
                {
                    var d=definitions.definitions[i];if(!depth && d.dimension=="depth")continue;
                    EditorGUILayout.LabelField(d.label_zh,
                        definitions.Control(i,result.targetCoordinates[i]).ToString("F2")+" → "+definitions.Control(i,result.fittedCoordinates[i]).ToString("F2")+
                        "；差 "+(100*Math.Abs(result.targetCoordinates[i]-result.fittedCoordinates[i])).ToString("F3")+" 脸宽百分点");
                }
                if(showCoordinates)EditorGUILayout.LabelField("控值使用跨模型相同的工程显示范围，不截断到 0–100；不能直接当作 Unity 权重。",EditorStyles.wordWrappedMiniLabel);
                showWeights=EditorGUILayout.Foldout(showWeights,"原生形态键：当前 → 建议权重",true);
                if(showWeights)for(int k=0;k<result.keys.Length;k++)EditorGUILayout.LabelField(result.keys[k],result.referenceWeights[k].ToString("F2")+" → "+result.weights[k].ToString("F2")+(result.weights[k]<.01 || result.weights[k]>99.99?" [触边]":""));
                EditorGUILayout.LabelField("应用仅写入本次候选形态键，未选中的权重保持当前值。",EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.BeginHorizontal();
                using(new EditorGUI.DisabledScope(fittedRoot==null || fittedRoot!=destinationRoot))
                    if(GUILayout.Button("应用到模型（可撤销）",GUILayout.Height(30)))Action(()=>ApplyResult());
                if(GUILayout.Button("保存拟合存档…",GUILayout.Height(30)))Action(()=>{var path=EditorUtility.SaveFilePanelInProject("保存拟合存档","模型拟合存档","asset","保存预览图片、坐标配置和完整结果。");if(path.Length>0){var saved=SaveArchive(path);CommonCoordinateArchiveWindow.ShowArchive(saved);}});
                EditorGUILayout.EndHorizontal();
                showExport=EditorGUILayout.Foldout(showExport,"导出结果",true);
                if(showExport)
                {
                    EditorGUILayout.BeginHorizontal();
                    if(GUILayout.Button("导出坐标目标…"))SaveTarget();
                    if(GUILayout.Button("导出完整拟合报告…"))SaveReport();
                    EditorGUILayout.EndHorizontal();
                    transition=EditorGUILayout.Toggle("从拟合前过渡到拟合后",transition);
                    animationDuration=EditorGUILayout.FloatField("动画时长（秒）",animationDuration);
                    if(GUILayout.Button("保存为动画…"))Action(()=>{var path=EditorUtility.SaveFilePanelInProject("保存拟合动画","模型拟合结果","anim","不勾选过渡时保存固定姿态；绑定路径相对于目标模型根对象。");if(path.Length>0)SaveAnimation(path,transition,animationDuration);});
                }
            }
            EditorGUILayout.EndScrollView();
        }
        private void DrawPreviews()
        {
            float width=Mathf.Max(100,(position.width-50)/3);float height=width*760/640;
            EditorGUILayout.BeginHorizontal();
            var textures=new[]{beforePreview,targetPreview,afterPreview};var labels=new[]{"拟合前","参考长相","拟合后"};
            for(int i=0;i<3;i++)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(width));EditorGUILayout.LabelField(labels[i],EditorStyles.miniLabel);
                Rect rect=GUILayoutUtility.GetRect(width,height);
                if(textures[i]!=null)GUI.DrawTexture(rect,textures[i],ScaleMode.ScaleToFit);else GUI.Label(rect,"坐标文件不包含参考渲染");
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        public void RunFit()
        {
            Clear();error="";
            var originals=UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true).Where(r=>r.gameObject.scene.IsValid()).ToArray();
            var snapshots=originals.Select(r=>r.sharedMesh==null?new float[0]:Enumerable.Range(0,r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray()).ToArray();
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).ToArray();var dirty=scenes.Select(s=>s.isDirty).ToArray();var selection=Selection.instanceIDs;
            try
            {
                definitions=CommonCoordinateDefinitions.Load(out definitionHash);
                var destination=CommonCoordinateModel.Capture(destinationRoot,definitionHash,ReadProfile(destinationProfile));
                CommonCoordinateModel source=null;double[,] points=null;double[] values;string sourceName;
                if(importedTarget==null)
                {
                    source=CommonCoordinateModel.Capture(sourceRoot,definitionHash,ReadProfile(sourceProfile));
                    if(!source.Names.SequenceEqual(destination.Names))throw new InvalidOperationException("两个模型语义点顺序不一致。");
                    points=source.anchors;values=definitions.Measure(points,source.Names);sourceName=source.profile.id;
                }
                else
                {
                    values=ReadValues(importedTarget,destination);sourceName="coordinate_file";
                    if(importedTarget.detail_points!=null && importedTarget.detail_points.Length>0)
                    {
                        if(importedTarget.detail_points.Length!=destination.Names.Length || importedTarget.detail_points.Select(p=>p.id).Distinct().Count()!=destination.Names.Length)
                            throw new InvalidOperationException("点细节必须包含完整且唯一的 42 个语义点。");
                        points=new double[destination.Names.Length,3];
                        for(int p=0;p<destination.Names.Length;p++)
                        {
                            var t=importedTarget.detail_points.FirstOrDefault(v=>v.id==destination.Names[p]);
                            if(t==null || !CommonCoordinateDefinitions.Finite(t.x) || !CommonCoordinateDefinitions.Finite(t.y) || !CommonCoordinateDefinitions.Finite(t.z))throw new InvalidOperationException("点细节无效。");
                            points[p,0]=t.x;points[p,1]=t.y;points[p,2]=t.z;
                        }
                        var measured=definitions.Measure(points,destination.Names);
                        // Edited controls may intentionally disagree with saved
                        // points. Reject rather than silently fitting two targets.
                        for(int f=0;f<values.Length;f++)if((depth || definitions.definitions[f].dimension!="depth") && Math.Abs(measured[f]-values[f])>1e-7)
                            throw new InvalidOperationException("坐标与附带点细节不一致："+definitions.definitions[f].id+"。编辑坐标后请移除 detail_points，或同步更新点。");
                    }
                }
                double detail=points!=null && lossMode!=1?1:0;
                double feature=detail==0?1:(lossMode==2?.25:0);
                result=CommonCoordinateSolver.Fit(destination,definitions,points,values,depth,allDetailPoints,detail,feature,priorMode==0?.002:.02,seed,
                    start=>EditorUtility.DisplayProgressBar("共同坐标拟合","初值 "+(start+1)+"/5",start/5f),
                    ()=>EditorUtility.DisplayCancelableProgressBar("共同坐标拟合","正在求解，可取消并保留已完成初值",.5f));
                result.sourceProfile=sourceName;result.definitionSha256=definitionHash;
                result.scope="Configured mesh and verified semantic anchors only; single-renderer linear BlendShapes in 0..100. No arbitrary photo or identity recovery guarantee.";
                fittedProfile=JsonUtility.FromJson<CommonCoordinateProfile>(JsonUtility.ToJson(destination.profile));
                fittedRoot=destinationRoot;fittedRendererPath=AnimationUtility.CalculateTransformPath(destination.renderer.transform,destinationRoot.transform);
                lastTarget=MakeTarget(destination.Names,values,points,sourceName);
                beforePreview=CommonCoordinatePreview.Capture(destination,destination.reference);
                if(source!=null)targetPreview=CommonCoordinatePreview.Capture(source,source.reference);
                afterPreview=CommonCoordinatePreview.Capture(destination,result.weights);
                scroll=Vector2.zero;
            }
            catch(Exception ex){error=ex.Message;Clear();Debug.LogError("共同坐标拟合："+ex.Message);}
            finally
            {
                EditorUtility.ClearProgressBar();
                sceneAudit=new CommonCoordinateSceneAudit{renderersChecked=originals.Length,
                    weightsUnchanged=originals.Select((r,i)=>r.sharedMesh==null || Enumerable.Range(0,r.sharedMesh.blendShapeCount).All(k=>r.GetBlendShapeWeight(k)==snapshots[i][k])).All(v=>v),
                    dirtyUnchanged=scenes.Select((s,i)=>s.isDirty==dirty[i]).All(v=>v),selectionUnchanged=selection.SequenceEqual(Selection.instanceIDs)};
                if(!sceneAudit.weightsUnchanged || !sceneAudit.dirtyUnchanged || !sceneAudit.selectionUnchanged)error+=" 场景状态核验失败，请检查外部脚本或动画更新。";
                Repaint();
            }
        }
        private double[] ReadValues(CommonCoordinateTarget target,CommonCoordinateModel destination)
        {
            if(target.schema!=1 || target.definition_sha256!=definitionHash || target.values==null
                || (target.unit!="shared_geometry" && target.unit!="common_controls"))throw new InvalidOperationException("坐标文件格式、单位或定义哈希不匹配。");
            if(target.values.Select(v=>v.id).Distinct().Count()!=target.values.Length || target.values.Any(v=>!CommonCoordinateDefinitions.Finite(v.value)))
                throw new InvalidOperationException("坐标含重复值或非有限数值。");
            var known=new HashSet<string>(definitions.definitions.Select(d=>d.id));
            if(target.values.Any(v=>!known.Contains(v.id)))throw new InvalidOperationException("坐标文件含未知特征名。");
            var values=definitions.Measure(destination.anchors,destination.Names);
            for(int f=0;f<values.Length;f++)
            {
                var d=definitions.definitions[f];var v=target.values.FirstOrDefault(t=>t.id==d.id);
                if(v==null){if(depth || d.dimension!="depth")throw new InvalidOperationException("缺少坐标："+d.id);continue;}
                var range=d.display_range;values[f]=target.unit=="shared_geometry"?v.value:range[0]+(range[1]-range[0])*v.value/100;
            }
            return values;
        }
        private CommonCoordinateTarget MakeTarget(string[] names,double[] values,double[,] points,string source)
        {
            return new CommonCoordinateTarget{definition_sha256=definitionHash,source=source,
                values=definitions.definitions.Select((d,i)=>new CommonCoordinateValue{id=d.id,value=values[i]}).ToArray(),
                detail_points=points==null?null:names.Select((id,i)=>new CommonCoordinatePoint{id=id,x=points[i,0],y=points[i,1],z=points[i,2]}).ToArray()};
        }
        public void SetCoordinateTarget(CommonCoordinateTarget target){importedTarget=target;Clear();}
        public void Configure(bool useDepth,bool allPoints,int loss,int prior,int randomSeed)
        {depth=useDepth;allDetailPoints=allPoints;lossMode=loss;priorMode=prior;seed=randomSeed;}
        public string ReportJson(){return JsonUtility.ToJson(new CommonCoordinateExport{fit=result,target=lastTarget,sceneAudit=sceneAudit},true);}
        private static CommonCoordinateProfile ReadProfile(TextAsset asset)
        {if(asset==null)return null;var p=CommonCoordinateProfileStore.LoadProfile(AssetDatabase.GetAssetPath(asset));if(p==null)throw new InvalidOperationException("坐标配置无效。");return p;}
        private void Action(System.Action action){try{error="";action();}catch(Exception ex){error=ex.Message;}}
        public void ApplyResult()
        {if(fittedRoot!=destinationRoot)throw new InvalidOperationException("目标模型已改变，请重新拟合。");CommonCoordinateResultActions.Apply(fittedRoot,fittedProfile,fittedRendererPath,result);}
        public void SaveAnimation(string path,bool animate,float duration)
        {if(fittedRoot!=destinationRoot)throw new InvalidOperationException("目标模型已改变，请重新拟合。");CommonCoordinateResultActions.SaveClip(path,fittedRoot,fittedProfile,fittedRendererPath,result,animate,duration);}
        public CommonCoordinateArchive SaveArchive(string path)
        {return CommonCoordinateResultActions.SaveArchive(path,fittedProfile,fittedRendererPath,new CommonCoordinateExport{fit=result,target=lastTarget,sceneAudit=sceneAudit},beforePreview,targetPreview,afterPreview);}
        public void SavePreviewFiles(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach(var pair in new[]{new KeyValuePair<string,Texture2D>("before",beforePreview),new KeyValuePair<string,Texture2D>("target",targetPreview),new KeyValuePair<string,Texture2D>("after",afterPreview)})
                if(pair.Value!=null)File.WriteAllBytes(Path.Combine(directory,pair.Key+".png"),pair.Value.EncodeToPNG());
        }
        private void Import()
        {
            string path=EditorUtility.OpenFilePanel("导入共同坐标（values 数组格式）","","json");if(string.IsNullOrEmpty(path))return;
            try{var target=JsonUtility.FromJson<CommonCoordinateTarget>(File.ReadAllText(path));if(target==null || target.values==null)throw new InvalidOperationException("需要 values:[{id,value}] 数组格式，可先从本窗口导出示例。");SetCoordinateTarget(target);error="";}
            catch(Exception ex){error="导入失败："+ex.Message;}
        }
        private void SaveTarget()
        {string path=EditorUtility.SaveFilePanel("导出共同坐标目标","","coordinate_target","json");if(!string.IsNullOrEmpty(path))File.WriteAllText(path,JsonUtility.ToJson(lastTarget,true));}
        private void SaveReport()
        {string path=EditorUtility.SaveFilePanel("导出全部初值、权重和误差","","common_coordinate_fit","json");if(!string.IsNullOrEmpty(path))File.WriteAllText(path,ReportJson());}
    }

    public static class CommonCoordinatePreview
    {
        public static Texture2D Capture(CommonCoordinateModel model,double[] weights)
        {
            var scene=EditorSceneManager.NewPreviewScene();Mesh mesh=null;var previous=RenderTexture.active;
            try
            {
                mesh=UnityEngine.Object.Instantiate(model.renderer.sharedMesh);mesh.ClearBlendShapes();mesh.vertices=model.PreviewVertices(weights);mesh.RecalculateNormals();mesh.RecalculateBounds();
                var go=new GameObject("__CommonCoordinateFace");SceneManager.MoveGameObjectToScene(go,scene);go.hideFlags=HideFlags.HideAndDontSave;go.layer=31;
                go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=model.renderer.sharedMaterials;
                var lights=UnityEngine.Object.FindObjectsOfType<Light>().Where(l=>l.enabled && l.gameObject.scene==model.renderer.gameObject.scene).ToArray();
                foreach(var light in lights)
                {
                    var lg=new GameObject("__CommonCoordinateLight");SceneManager.MoveGameObjectToScene(lg,scene);lg.hideFlags=HideFlags.HideAndDontSave;
                    var copy=lg.AddComponent<Light>();EditorUtility.CopySerialized(light,copy);copy.cullingMask=1<<31;lg.transform.SetPositionAndRotation(light.transform.position,light.transform.rotation);
                }
                if(lights.Length==0)
                {var lg=new GameObject("__CommonCoordinateLight");SceneManager.MoveGameObjectToScene(lg,scene);var l=lg.AddComponent<Light>();l.type=LightType.Directional;l.intensity=1.2f;l.cullingMask=1<<31;lg.transform.rotation=Quaternion.Euler(20,25,0);}
                var cg=new GameObject("__CommonCoordinateCamera");SceneManager.MoveGameObjectToScene(cg,scene);cg.hideFlags=HideFlags.HideAndDontSave;
                var camera=cg.AddComponent<Camera>();camera.enabled=false;camera.scene=scene;camera.cullingMask=1<<31;camera.orthographic=true;camera.orthographicSize=.8f;
                camera.aspect=640f/760;camera.nearClipPlane=.1f;camera.farClipPlane=10;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.18f,.19f,.21f);
                camera.transform.position=new Vector3(0,.43f,-3);camera.transform.rotation=Quaternion.identity;
                return AvatarFitRenderer.RenderWith(camera,640,760,null);
            }
            finally{RenderTexture.active=previous;EditorSceneManager.ClosePreviewScene(scene);if(mesh!=null)UnityEngine.Object.DestroyImmediate(mesh);}
        }
    }
}
