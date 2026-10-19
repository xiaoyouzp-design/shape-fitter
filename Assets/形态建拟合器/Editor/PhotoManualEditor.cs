using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public partial class ModelCardFitWindow
    {
        [SerializeField] private bool showPhotoDetailPoints=true;
        [SerializeField] private bool photoManualMore;
        private static readonly string[] ManualRegions={"画面左眼","画面右眼","画面左眉","画面右眉","嘴部","脸廓","鼻尖"};
        private int[] VisiblePhotoPointIndices()
        {
            if(photoTemplate==null)return new int[0];
            return PhotoDetailLandmarks.OrderedRegion(photoEditRegion,showPhotoDetailPoints)
                .Select(id=>Array.FindIndex(photoTemplate.points,p=>p.id==id)).Where(i=>i>=0).ToArray();
        }
        private void DrawManualPhotoEditor()
        {
            if(photoTemplate==null)return;
            var modes=new[]{4,0,1,2,3};
            int mode=modes[GUILayout.Toolbar(Mathf.Max(0,Array.IndexOf(modes,photoEditMode)),new[]{"描边","拖动","区域","补点","删除"},GUILayout.Height(26))];
            if(mode!=photoEditMode){EndPhotoDrag(true);EndPhotoTraceDrag(false);photoEditMode=mode;}
            EditorGUILayout.BeginHorizontal();
            int region=EditorGUILayout.Popup("部位",Mathf.Clamp(photoEditRegion,0,6),ManualRegions);
            if(region!=photoEditRegion){EndPhotoDrag(true);EndPhotoTraceDrag(false);photoEditRegion=region;SelectNextManualPoint();}
            if(photoEditMode==2){bool detail=GUILayout.Toggle(showPhotoDetailPoints,new GUIContent("细分点","显示可选细分点；切换不会删除已标点"),GUILayout.Width(72));
            if(detail!=showPhotoDetailPoints){showPhotoDetailPoints=detail;SelectNextManualPoint();}}
            EditorGUILayout.LabelField("已标 "+photoAnnotation.points.Count(p=>p.state=="marked")+" / "+photoTemplate.points.Length,GUILayout.Width(110));
            EditorGUILayout.EndHorizontal();
            if(HasPendingPhotoTrace())EditorGUILayout.LabelField("待采用描边："+PendingPhotoTraceRegions(),EditorStyles.wordWrappedMiniLabel);
            if(photoEditMode==4)DrawPhotoTraceControls();
            else if(photoEditMode==2)
            {
                var indices=VisiblePhotoPointIndices();
                if(indices.Length>0&&!indices.Contains(activePhotoPoint))SelectNextManualPoint();
                // A compact local list replaces the global 42/80-item dropdown.
                const int columns=4;
                var pointButton=new GUIStyle(GUI.skin.button){wordWrap=true};
                float buttonWidth=Mathf.Max(100,(position.width-42)/columns);
                for(int row=0;row<indices.Length;row+=columns)
                {
                    EditorGUILayout.BeginHorizontal();
                    for(int c=0;c<columns&&row+c<indices.Length;c++)
                    {
                        int index=indices[row+c];var definition=photoTemplate.points[index];var mark=photoAnnotation.points.Find(p=>p.id==definition.id);
                        string status=mark==null||mark.state=="unmarked"?"＋ ":mark.state=="skipped"?"－ ":"✓ ";
                        string label=PhotoDetailLandmarks.ShortLabel(definition.label).Replace("（细分）","").Replace("上眼睑外侧四分点","上外侧").Replace("上眼睑内侧四分点","上内侧").Replace("上眼睑中点","上中").Replace("下眼睑外侧四分点","下外侧").Replace("下眼睑内侧四分点","下内侧").Replace("下眼睑中点","下中");
                        var color=GUI.backgroundColor;if(index==activePhotoPoint)GUI.backgroundColor=new Color(.4f,.8f,1f);
                        if(GUILayout.Button(new GUIContent(status+label,definition.label),pointButton,GUILayout.Width(buttonWidth),GUILayout.MinHeight(25)))activePhotoPoint=index;
                        GUI.backgroundColor=color;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("点击图片标记："+photoTemplate.points[Mathf.Clamp(activePhotoPoint,0,photoTemplate.points.Length-1)].label,EditorStyles.wordWrappedMiniLabel);
                if(GUILayout.Button("跳过",GUILayout.Width(60)))SetPhotoPointState("skipped");
                EditorGUILayout.EndHorizontal();
                if(PhotoDetailLandmarks.IsDetail(photoTemplate.points[activePhotoPoint].id))
                    EditorGUILayout.LabelField("细分点的模型对应为相邻锚点平均，属于近似；只标可见位置，不会自动补点。",EditorStyles.wordWrappedMiniLabel);
            }
            else EditorGUILayout.LabelField(photoEditMode==3?"点击图片上的点即可删除；Ctrl+Z 撤销。也可在任意模式右键点删除。":photoEditMode==1?(photoEditRegion==6?"鼻尖是独立点，请切换“拖动”直接调整。":"拖框内移动整组，拖四角缩放；Esc 取消，Ctrl+Z 撤销。"):"拖控制点或线段调整；右键点删除。缺失轮廓用“描边”，单个点用“补点”。",EditorStyles.wordWrappedMiniLabel);
            photoManualMore=EditorGUILayout.Foldout(photoManualMore,"更多：标注文件与显示",true);
            if(photoManualMore)
            {
                EditorGUILayout.BeginHorizontal();
                if(GUILayout.Button("导出标注…"))SavePhotoAnnotations();
                if(GUILayout.Button("导入标注…"))LoadPhotoAnnotations();
                if(GUILayout.Button("清空（可撤销）")){Undo.RecordObject(this,"清空图片标注");photoAnnotation.points.Clear();if(photoAnnotation.contourTraces!=null)photoAnnotation.contourTraces.Clear();RebuildPhotoLandmarks();ClearFitResult();}
                EditorGUILayout.EndHorizontal();
                showModelPreview=EditorGUILayout.Toggle("显示只读模型预览",showModelPreview);
                EditorGUILayout.LabelField("细分点不增加独立的模型形变自由度，可能带来对应偏差。自动引擎仍只输出它支持的语义点。",EditorStyles.wordWrappedMiniLabel);
            }
        }
        private void SelectNextManualPoint()
        {
            var indices=VisiblePhotoPointIndices();if(indices.Length==0)return;
            activePhotoPoint=indices.FirstOrDefault(i=>{var p=photoAnnotation.points.Find(m=>m.id==photoTemplate.points[i].id);return p==null||p.state=="unmarked";});
            if(!indices.Contains(activePhotoPoint))activePhotoPoint=indices[0];
        }
        private void DeletePhotoMark(string id)
        {
            if(!photoAnnotation.points.Any(p=>p.id==id&&p.state=="marked"))return;
            Undo.RecordObject(this,"删除图片特征点");photoAnnotation.points.RemoveAll(p=>p.id==id);
            RebuildPhotoLandmarks();ClearFitResult();Repaint();
        }
        private void HandlePhotoPointDeletion(Rect imageRect,bool inside)
        {
            var e=Event.current;
            if(photoEditMode==4||!inside||photoTemplate==null||!photoOnly||e.type!=EventType.MouseDown||GUIUtility.hotControl!=0||!(e.button==1||(e.button==0&&photoEditMode==3)))return;
            PhotoMark hit=null;float nearest=10;
            foreach(var p in photoAnnotation.points.Where(p=>p.state=="marked"))
            {
                float d=Vector2.Distance(e.mousePosition,PhotoContourGeometry.ToGui(new Vector2(p.x,p.y),imageRect,imageTex.width,imageTex.height));
                if(d<nearest){nearest=d;hit=p;}
            }
            if(hit==null)return;
            string id=hit.id;activePhotoPoint=Array.FindIndex(photoTemplate.points,p=>p.id==id);
            if(e.button==0)DeletePhotoMark(id);
            else{var menu=new GenericMenu();menu.AddItem(new GUIContent("删除这个标注点"),false,()=>DeletePhotoMark(id));menu.ShowAsContext();}
            e.Use();Repaint();
        }
    }
}
