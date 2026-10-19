using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public class CommonCoordinateBuilderWindow:EditorWindow
    {
        [SerializeField] private SkinnedMeshRenderer face;
        [SerializeField] private CommonCoordinateProfile draft;
        [SerializeField] private int active;
        [SerializeField] private bool picking;
        [SerializeField] private CoordinateAutoResult automatic;
        [SerializeField] private SkinnedMeshRenderer referenceFace;
        [SerializeField] private Transform targetHead;
        [SerializeField] private string lastArchivePath="",lastArchivedSignature="";
        private string lastAttemptSignature="";
        private double nextArchiveCheck;
        private bool showArchiveHistory;
        private CoordinateProfileRecord[] archiveHistory=new CoordinateProfileRecord[0];
        public string LastArchivePath { get { return lastArchivePath; } }
        private string error="",search="";
        private Vector2 scroll,keysScroll;
        private bool showKeys,showManual,showAutomaticSettings;
        [MenuItem("形态键拟合/构建模型坐标",false,30)]
        public static void Open(){var w=GetWindow<CommonCoordinateBuilderWindow>("构建模型坐标");w.minSize=new Vector2(540,580);w.Show();}
        private void OnEnable()
        {
            SceneView.duringSceneGui+=SceneGUI;
            if(automatic!=null&&string.IsNullOrEmpty(automatic.method))automatic=null;
            if(face!=null)archiveHistory=CommonCoordinateProfileStore.Records().Where(r=>r.profile.Matches(face.sharedMesh)).ToArray();
        }
        private void OnDisable(){SceneView.duringSceneGui-=SceneGUI;FlushArchive();}
        private void Update()
        {
            if(EditorApplication.timeSinceStartup<nextArchiveCheck)return;
            nextArchiveCheck=EditorApplication.timeSinceStartup+.8;FlushArchive();
        }
        private void FlushArchive()
        {
            if(face==null||draft==null)return;
            var signature=JsonUtility.ToJson(draft);
            if(signature==lastArchivedSignature||signature==lastAttemptSignature)return;
            Try(()=>ArchiveNow());Repaint();
        }
        public CoordinateProfileRecord ArchiveNow()
        {
            if(face==null||draft==null)throw new InvalidOperationException("没有坐标可以归档。");
            lastAttemptSignature=JsonUtility.ToJson(draft);
            var record=CommonCoordinateProfileStore.Save(face,draft,automatic);
            lastArchivedSignature=JsonUtility.ToJson(draft);lastArchivePath=record.assetPath;
            archiveHistory=CommonCoordinateProfileStore.Records().Where(r=>r.profile.Matches(face.sharedMesh)).ToArray();
            error="已自动归档（"+record.status+"）"+(record.validationError.Length>0?"："+record.validationError:"");return record;
        }
        public static CommonCoordinateProfile NewProfile(SkinnedMeshRenderer renderer)
        {
            if(renderer==null || renderer.sharedMesh==null)throw new InvalidOperationException("请选择脸部 SkinnedMeshRenderer。");
            var mesh=renderer.sharedMesh;var triangles=mesh.triangles;var bytes=new byte[triangles.Length*4];Buffer.BlockCopy(triangles,0,bytes,0,bytes.Length);
            string hash;CommonCoordinateDefinitions.Load(out hash);
            return new CommonCoordinateProfile{schema=2,id="custom_"+Guid.NewGuid().ToString("N"),description=CommonCoordinateProfileStore.ModelName(renderer)+" / "+mesh.name,vertexCount=mesh.vertexCount,blendShapeCount=mesh.blendShapeCount,
                definitionSha256=hash,topologySha256=PhotoLandmarkTemplate.Hash(bytes),shapeNamesSha256=PhotoLandmarkTemplate.Hash(Encoding.UTF8.GetBytes(string.Join("|",Enumerable.Range(0,mesh.blendShapeCount).Select(mesh.GetBlendShapeName)))),
                provenance="Manual semantic anchors; Unity BakeMesh calibration; neutral mesh face basis; native weights 0..100.",keys=new string[0],
                points=PhotoLandmarkTemplate.Load().points.Select(p=>new PhotoLandmarkDefinition{id=p.id,label=p.label,vertices=new int[0],weights=new float[0]}).ToArray()};
        }
        public void Configure(SkinnedMeshRenderer renderer,CommonCoordinateProfile profile=null)
        {
            FlushArchive();
            var next=profile==null?NewProfile(renderer):JsonUtility.FromJson<CommonCoordinateProfile>(JsonUtility.ToJson(profile));
            var canonical=PhotoLandmarkTemplate.Load().points.Select(p=>p.id).ToArray();
            if(renderer==null||!next.Matches(renderer.sharedMesh)||next.keys==null||next.points==null||next.points.Length!=canonical.Length
                ||!next.points.Select(p=>p.id).OrderBy(id=>id).SequenceEqual(canonical.OrderBy(id=>id))
                ||next.keys.Any(k=>renderer.sharedMesh.GetBlendShapeIndex(k)<0)||next.keys.Distinct().Count()!=next.keys.Length
                ||next.points.Any(p=>p.vertices==null||p.weights==null||p.vertices.Length!=p.weights.Length||p.vertices.Any(v=>v<0||v>=next.vertexCount)
                    ||p.weights.Any(w=>!CommonCoordinateDefinitions.Finite(w)||w<0)||(p.weights.Length>0&&Math.Abs(p.weights.Sum()-1)>1e-4)))
                throw new InvalidOperationException("草稿的语义点、顶点或候选键无效。");
            next.points=canonical.Select(id=>next.points.First(p=>p.id==id)).ToArray();
            face=renderer;draft=next;active=0;error="";picking=false;automatic=null;Repaint();
            lastArchivePath="";lastArchivedSignature=JsonUtility.ToJson(draft);lastAttemptSignature="";
            archiveHistory=CommonCoordinateProfileStore.Records().Where(r=>r.profile.Matches(renderer.sharedMesh)).ToArray();
        }
        public CoordinateAutoResult GenerateAutomatically(bool forceTransfer=false)
        {
            CommonCoordinateProfile preferred=null;
            if(draft!=null&&face!=null&&draft.Matches(face.sharedMesh))
                try{string hash;CommonCoordinateDefinitions.Load(out hash);draft.Validate(hash);preferred=draft;}catch(InvalidOperationException){}
            var generated=CommonCoordinateAuto.Generate(face,referenceFace,targetHead,forceTransfer,preferred);
            if(draft!=null&&draft.Matches(face.sharedMesh))
            {
                if(draft.keys.Length>0)generated.profile.keys=(string[])draft.keys.Clone();
                var preserve=draft.manuallyAdjustedPoints??(string.IsNullOrEmpty(draft.generationMethod)?draft.points.Where(p=>p.vertices.Length>0).Select(p=>p.id).ToArray():new string[0]);
                foreach(var id in preserve)
                {
                    var old=draft.points.FirstOrDefault(p=>p.id==id);var next=generated.profile.points.FirstOrDefault(p=>p.id==id);
                    if(old==null||next==null||old.vertices.Length==0)continue;
                    next.vertices=(int[])old.vertices.Clone();next.weights=(float[])old.weights.Clone();
                }
                generated.profile.manuallyAdjustedPoints=preserve;
                CommonCoordinateAuto.RefreshPreview(generated,face,targetHead);
            }
            int schema=generated.profile.schema;
            Configure(face,generated.profile);if(generated.method=="exact_profile")draft.schema=schema;
            automatic=generated;automatic.profile=draft;ArchiveNow();SceneView.RepaintAll();return automatic;
        }
        private void OnGUI()
        {
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("为新模型建立一次坐标配置",EditorStyles.largeLabel);
            EditorGUILayout.HelpBox("选择脸部网格 → 自动生成并归档。名称包含模型名；检查和微调也会自动保留新版本，无需手动保存。",MessageType.Info);
            var next=(SkinnedMeshRenderer)EditorGUILayout.ObjectField("脸部网格",face,typeof(SkinnedMeshRenderer),true);
            if(next!=face){FlushArchive();face=next;draft=null;automatic=null;targetHead=null;picking=false;error="";lastArchivePath="";lastArchivedSignature="";lastAttemptSignature="";}
            if(face!=null)EditorGUILayout.LabelField("模型名称",CommonCoordinateProfileStore.ModelName(face));
            showAutomaticSettings=EditorGUILayout.Foldout(showAutomaticSettings,"自动设置（通常无需修改）",true);
            if(showAutomaticSettings)
            {
                referenceFace=(SkinnedMeshRenderer)EditorGUILayout.ObjectField("已配置的参考脸部",referenceFace,typeof(SkinnedMeshRenderer),true);
                targetHead=(Transform)EditorGUILayout.ObjectField("目标头骨（可选）",targetHead,typeof(Transform),true);
                EditorGUILayout.LabelField("未指定参考时从场景寻找已有配置。请让两个模型正面朝根对象 +Z、上方 +Y，采用相近的中性表情和姿态。",EditorStyles.wordWrappedMiniLabel);
            }
            using(new EditorGUI.DisabledScope(face==null || face.sharedMesh==null))
            {
                if(GUILayout.Button("自动生成坐标并归档",GUILayout.Height(34)))Try(()=>GenerateAutomatically());
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button("新建空白配置（手动）"))Try(()=>Configure(face));
                if(GUILayout.Button("载入坐标／存档…"))Try(()=>{var path=EditorUtility.OpenFilePanel("载入模型坐标","","json");if(path.Length==0)return;var p=CommonCoordinateProfileStore.LoadProfile(path);if(p==null||!p.Matches(face.sharedMesh))throw new InvalidOperationException("配置网格不匹配。");Configure(face,p);});
                EditorGUILayout.EndHorizontal();
            }
            if(draft!=null && face!=null)
            {
                draft.description=EditorGUILayout.TextField("配置名称",draft.description);
                if(automatic!=null)
                {
                    EditorGUILayout.HelpBox(automatic.explanation,automatic.method=="exact_profile"?MessageType.Info:MessageType.Warning);
                    EditorGUILayout.LabelField("坐标来源",automatic.reference+(automatic.method=="exact_profile"?" · 已有配置复用":" · 自动表面迁移候选"));
                    DrawAutomaticPreview();
                }
                if(draft.automaticReviewRequired)
                {
                    EditorGUILayout.HelpBox("自动点并非已确认的解剖位置。请检查眼睑、嘴唇、眉毛和脸廓，并核对自动筛选的候选键是否包含不需要的表情。",MessageType.Warning);
                    bool reviewed=EditorGUILayout.ToggleLeft("我已检查自动坐标及候选键（自动校验并归档为可用配置）",draft.automaticReviewed);
                    if(reviewed!=draft.automaticReviewed){draft.automaticReviewed=reviewed;Try(()=>ArchiveNow());}
                }
                EditorGUILayout.LabelField("标记进度",draft.points.Count(p=>p.vertices.Length>0)+" / "+draft.points.Length+"；左右按模型正面画面方向");
                showManual=EditorGUILayout.Foldout(showManual,"检查／微调个别点（可选）",true);
                if(showManual)
                {
                active=EditorGUILayout.Popup("当前点",active,draft.points.Select(p=>p.label+(p.vertices.Length>0?" ✓":" · 未标")).ToArray());
                picking=GUILayout.Toggle(picking,"在场景视图点击标记（Alt 拖动仍可旋转视图）","Button",GUILayout.Height(30));
                if(GUILayout.Button("定位脸部网格")){var view=SceneView.lastActiveSceneView;if(view!=null)view.Frame(face.bounds,false);}
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button("清除当前点")){var id=draft.points[active].id;draft.points[active].vertices=new int[0];draft.points[active].weights=new float[0];draft.manuallyAdjustedPoints=(draft.manuallyAdjustedPoints??new string[0]).Where(p=>p!=id).ToArray();draft.automaticReviewed=false;SceneView.RepaintAll();}
                EditorGUILayout.LabelField("微调后自动归档",EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                }
                else picking=false;
                showKeys=EditorGUILayout.Foldout(showKeys,"参与拟合的形态键（已选 "+draft.keys.Length+"）",true);
                if(showKeys)
                {
                    EditorGUILayout.HelpBox("只选用于脸型的键。表情、口型及相互冲突的键应谨慎选择。支持单个正权重帧、0–100 范围；多帧键不可选。",MessageType.Info);
                    search=EditorGUILayout.TextField("搜索",search);keysScroll=EditorGUILayout.BeginScrollView(keysScroll,GUILayout.Height(200));
                    var mesh=face.sharedMesh;var selected=draft.keys.ToList();
                    for(int i=0;i<mesh.blendShapeCount;i++)
                    {
                        var name=mesh.GetBlendShapeName(i);if(name.IndexOf(search,StringComparison.OrdinalIgnoreCase)<0)continue;
                        bool supported=mesh.GetBlendShapeFrameCount(i)==1&&mesh.GetBlendShapeFrameWeight(i,0)>0;
                        using(new EditorGUI.DisabledScope(!supported))
                        {bool value=EditorGUILayout.ToggleLeft(name+(supported?"":"（多帧／非正帧，不支持）"),selected.Contains(name));if(value&&!selected.Contains(name))selected.Add(name);else if(!value)selected.Remove(name);}
                    }
                    draft.keys=selected.ToArray();EditorGUILayout.EndScrollView();
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("自动归档位置",CommonCoordinateProfileStore.Root);
                if(!string.IsNullOrEmpty(lastArchivePath))
                {
                    EditorGUILayout.LabelField(Path.GetFileName(lastArchivePath),EditorStyles.wordWrappedMiniLabel);
                    if(GUILayout.Button("在工程中定位最新坐标存档"))EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<TextAsset>(lastArchivePath));
                }
                if(!string.IsNullOrEmpty(lastAttemptSignature)&&lastAttemptSignature!=lastArchivedSignature&&GUILayout.Button("重试自动归档"))Try(()=>ArchiveNow());
                showArchiveHistory=EditorGUILayout.Foldout(showArchiveHistory,"本模型坐标归档（"+archiveHistory.Length+"）",true);
                if(showArchiveHistory)foreach(var record in archiveHistory)
                    if(GUILayout.Button(Path.GetFileName(record.assetPath)))Try(()=>{Configure(face,record.profile);lastArchivePath=record.assetPath;});
                EditorGUILayout.LabelField("配置仅描述所选网格。多网格脸部需要合并对应方案；当前不会同时拟合多个网格。",EditorStyles.wordWrappedMiniLabel);
            }
            if(error.Length>0)EditorGUILayout.HelpBox(error,MessageType.Info);
            EditorGUILayout.EndScrollView();
        }
        private void DrawAutomaticPreview()
        {
            if(automatic.diagnostics==null||automatic.diagnostics.Length==0)return;
            EditorGUILayout.LabelField("自动生成时的正面候选示意 · 橙色重点检查，修改后以场景标点为准",EditorStyles.wordWrappedMiniLabel);
            var r=GUILayoutUtility.GetRect(260,260);EditorGUI.DrawRect(r,new Color(.12f,.13f,.15f));
            Func<Vector3,Vector2> project=p=>new Vector2(r.center.x-p.x*230,r.center.y-p.y*230);
            foreach(var vertex in automatic.previewVertices){var v=project(vertex);EditorGUI.DrawRect(new Rect(v.x,v.y,1,1),Color.gray);}
            for(int i=0;i<automatic.diagnostics.Length;i++)
            {
                var p=automatic.diagnostics[i];var v=project(p.normalizedPosition);
                EditorGUI.DrawRect(new Rect(v.x-2,v.y-2,4,4),p.needsAttention?new Color(1,.6f,.15f):Color.cyan);
                if(i==active)GUI.Label(new Rect(v.x+6,v.y,200,20),p.label);
                if(Event.current.type==EventType.MouseDown&&Event.current.button==0&&Vector2.Distance(Event.current.mousePosition,v)<7){active=i;showManual=true;Event.current.Use();Repaint();}
            }
            EditorGUILayout.LabelField("初始表面投影距离："+automatic.diagnostics[active].surfaceDistance.ToString("F4")+"（头部归一化单位，不是语义置信度）",EditorStyles.wordWrappedMiniLabel);
        }
        public void SaveProfile(string path)
        {
            if(!path.StartsWith("Assets/",StringComparison.Ordinal)||!path.EndsWith("CoordinateProfile.json",StringComparison.OrdinalIgnoreCase)||path.Contains(".."))throw new InvalidOperationException("请保存到 Assets 内，并使用 *CoordinateProfile.json 文件名。");
            if(File.Exists(path))throw new InvalidOperationException("该配置已存在，请另取文件名以保留旧配置。");
            string definitionHash;CommonCoordinateDefinitions.Load(out definitionHash);
            var calibrated=CommonCoordinateModel.Capture(face.gameObject,definitionHash,draft);
            File.WriteAllText(path,JsonUtility.ToJson(draft,true));AssetDatabase.ImportAsset(path);picking=false;
            error="已保存，采样校验误差 "+calibrated.bakeMaxError.ToString("G4")+"。现在可在模型拟合中使用。";
        }
        private void Try(Action action){try{error="";action();}catch(Exception ex){error=ex.Message;}}
        private void SceneGUI(SceneView view)
        {
            if(face==null||draft==null)return;
            var mesh=new Mesh();
            try{face.BakeMesh(mesh);var vertices=mesh.vertices;for(int i=0;i<draft.points.Length;i++){
                var p=draft.points[i];if(p.vertices.Length==0||p.vertices.Any(v=>v>=vertices.Length))continue;
                var world=face.transform.TransformPoint(CommonCoordinateCalibration.Point(vertices,p));Handles.color=i==active?Color.yellow:(draft.automaticReviewRequired&&!draft.automaticReviewed?new Color(1,.6f,.15f):Color.cyan);
                Handles.DotHandleCap(0,world,Quaternion.identity,HandleUtility.GetHandleSize(world)*.015f,EventType.Repaint);
                if(i==active)Handles.Label(world,p.label);
            }}finally{DestroyImmediate(mesh);}
            var e=Event.current;if(!picking||e.alt)return;
            if(e.type==EventType.Layout)HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if(e.type!=EventType.MouseDown||e.button!=0)return;
            int[] ids;float[] weights;
            if(CommonCoordinateCalibration.Pick(face,HandleUtility.GUIPointToWorldRay(e.mousePosition),out ids,out weights))
            {
                draft.points[active].vertices=ids;draft.points[active].weights=weights;
                draft.manuallyAdjustedPoints=(draft.manuallyAdjustedPoints??new string[0]).Concat(new[]{draft.points[active].id}).Distinct().ToArray();draft.automaticReviewed=false;
                if(automatic!=null)CommonCoordinateAuto.RefreshPreview(automatic,face,targetHead);
                for(int offset=1;offset<=draft.points.Length;offset++){int i=(active+offset)%draft.points.Length;if(draft.points[i].vertices.Length==0){active=i;break;}}
                error="已标记。请核对点是否位于所需的眼睑、嘴唇或脸廓表面。";Repaint();view.Repaint();
            }
            e.Use();
        }
    }
}
