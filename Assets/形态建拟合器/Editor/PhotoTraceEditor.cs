using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public partial class ModelCardFitWindow
    {
        [SerializeField] private List<PhotoTraceDraft> photoTraceDrafts=new List<PhotoTraceDraft>();
        [SerializeField] private bool photoTraceDetailed=true;
        [NonSerialized] private int traceDragIndex=-1,traceDragControl,traceUndoGroup;
        [NonSerialized] private PhotoTraceDraft traceDragging;
        [NonSerialized] private Vector2 traceDragOriginal;

        private bool HasPendingPhotoTrace()
        {return photoAnnotation!=null&&photoTraceDrafts!=null&&photoTraceDrafts.Any(d=>d.imageSha256==photoAnnotation.imageSha256&&d.vertices!=null&&d.vertices.Count>0);}
        private string PendingPhotoTraceRegions()
        {return string.Join("、",photoTraceDrafts.Where(d=>d.imageSha256==photoAnnotation.imageSha256&&d.vertices!=null&&d.vertices.Count>0).Select(d=>ManualRegions[Mathf.Clamp(d.region,0,6)]).Distinct().ToArray());}
        private PhotoTraceDraft CurrentPhotoTrace(bool create)
        {
            if(photoAnnotation==null)return null;
            if(photoTraceDrafts==null)photoTraceDrafts=new List<PhotoTraceDraft>();
            var draft=photoTraceDrafts.FirstOrDefault(d=>d.region==photoEditRegion&&d.imageSha256==photoAnnotation.imageSha256);
            if(draft==null&&create){draft=new PhotoTraceDraft{region=photoEditRegion,imageSha256=photoAnnotation.imageSha256};photoTraceDrafts.Add(draft);}
            return draft;
        }
        private void ResetPhotoTraces(){EndPhotoTraceDrag(false);photoTraceDrafts=new List<PhotoTraceDraft>();}
        private PhotoMark[] PreviewPhotoTrace(out string error)
        {
            error="";var draft=CurrentPhotoTrace(false);
            if(draft==null||!draft.ready||imageTex==null)return null;
            try{return PhotoTraceSampler.Sample(draft.region,draft.vertices,imageTex.width,imageTex.height,photoTraceDetailed);}
            catch(Exception ex){error=ex.Message;return null;}
        }
        private void DrawPhotoTraceControls()
        {
            var draft=CurrentPhotoTrace(false);int count=draft==null?0:draft.vertices.Count;
            string guide=photoEditRegion==6?"在图片上点击鼻尖即可。":photoEditRegion==4?"沿嘴部开口边缘（上下唇交界）描一圈，不画唇部外缘；双击或回车完成。闭嘴太细时请用补点。":PhotoTraceSampler.Closed(photoEditRegion)?"沿眼睛开口边缘连续点击，不沿睫毛尖或眼影描边。起点和顺逆时针不限；双击或回车完成。":photoEditRegion==5?"从一侧脸颊沿下缘，经下巴画到另一侧脸颊，不封口；双击或回车完成。":"沿眉毛中线从一端连续点到另一端，不封口；双击或回车完成。";
            EditorGUILayout.LabelField(guide,EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            using(new EditorGUI.DisabledScope(count==0))
            {
                if(GUILayout.Button("退一步"))RemoveLastPhotoTraceVertex();
                if(GUILayout.Button("重新描绘"))ClearCurrentPhotoTrace();
            }
            using(new EditorGUI.DisabledScope(count<PhotoTraceSampler.Minimum(photoEditRegion)))
                if(GUILayout.Button("完成 / 预览采样"))FinishPhotoTrace();
            photoTraceDetailed=GUILayout.Toggle(photoTraceDetailed,"细分采样",GUILayout.Width(85));
            EditorGUILayout.EndHorizontal();
            string error;var preview=PreviewPhotoTrace(out error);
            if(draft!=null&&draft.ready)
            {
                // Keep the viewport fixed while dragging a draft between valid/invalid shapes.
                var panel=GUILayoutUtility.GetRect(0,74,GUILayout.ExpandWidth(true));
                if(error.Length>0)EditorGUI.HelpBox(panel,error,MessageType.Warning);
                else if(preview!=null)
                {
                    var ids=PhotoDetailLandmarks.OrderedRegion(photoEditRegion,true);
                    int old=photoAnnotation.points.Count(p=>ids.Contains(p.id)&&p.state=="marked");
                    GUI.Label(new Rect(panel.x,panel.y,panel.width,40),"紫点为轮廓采样预览，共 "+preview.Length+" 点；采用后替换本部位已有 "+old+" 点。可先拖动橙色折点修正。",EditorStyles.wordWrappedMiniLabel);
                    if(GUI.Button(new Rect(panel.x,panel.y+42,panel.width,28),"采用这条轮廓（可撤销）"))ApplyPhotoTrace();
                }
            }
            else EditorGUILayout.LabelField("已画 "+count+" 个折点；至少 "+PhotoTraceSampler.Minimum(photoEditRegion)+" 个。右键 / Backspace 退一步，Esc 清除草稿。",EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("橙线是草稿，采用后才参与拟合及标注导出；采样不等于自动识别。遮挡部位请保留为空。",EditorStyles.wordWrappedMiniLabel);
        }
        private void FinishPhotoTrace()
        {
            var draft=CurrentPhotoTrace(false);if(draft==null)return;
            Undo.RecordObject(this,"完成轮廓草稿");draft.ready=true;Repaint();
        }
        private void RemoveLastPhotoTraceVertex()
        {
            var draft=CurrentPhotoTrace(false);if(draft==null||draft.vertices.Count==0)return;
            Undo.RecordObject(this,"回退轮廓草稿");draft.vertices.RemoveAt(draft.vertices.Count-1);draft.ready=false;Repaint();
        }
        private void ClearCurrentPhotoTrace()
        {
            EndPhotoTraceDrag(false);var draft=CurrentPhotoTrace(false);if(draft==null)return;
            Undo.RecordObject(this,"清除轮廓草稿");draft.vertices.Clear();draft.ready=false;Repaint();
        }
        private void ApplyPhotoTrace()
        {
            string error;var preview=PreviewPhotoTrace(out error);if(preview==null)throw new InvalidOperationException(error.Length>0?error:"请先完成轮廓并预览。");
            if(photoTemplate==null||preview.Any(p=>!photoTemplate.points.Any(d=>d.id==p.id)))throw new InvalidOperationException("当前模型尚无完整的轮廓对应关系。");
            var draft=CurrentPhotoTrace(false);var ids=PhotoDetailLandmarks.OrderedRegion(photoEditRegion,true);
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("采用图片轮廓");Undo.RecordObject(this,"采用图片轮廓");
            photoAnnotation.points.RemoveAll(p=>ids.Contains(p.id));
            foreach(var p in preview)photoAnnotation.Set(p.id,p.x,p.y,"marked");
            photoAnnotation.templateId=photoTemplate.id;
            if(photoAnnotation.contourTraces==null)photoAnnotation.contourTraces=new List<PhotoTraceRecord>();
            photoAnnotation.contourTraces.RemoveAll(p=>p.region==photoEditRegion);
            photoAnnotation.contourTraces.Add(new PhotoTraceRecord{region=photoEditRegion,imageSha256=photoAnnotation.imageSha256,
                createdUtc=DateTime.UtcNow.ToString("o"),detailed=photoTraceDetailed,sourceVertices=draft.vertices.ToArray(),sampledPoints=preview});
            // The source stroke remains in the annotation provenance. Clear only this editor draft.
            draft.vertices.Clear();draft.ready=false;photoEditMode=0;
            RebuildPhotoLandmarks();ClearFitResult();Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);Repaint();
        }
        private void EndPhotoTraceDrag(bool cancel)
        {
            if(traceDragging==null)return;
            if(cancel&&traceDragIndex>=0&&traceDragIndex<traceDragging.vertices.Count)traceDragging.vertices[traceDragIndex]=traceDragOriginal;
            Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(traceUndoGroup);
            if(GUIUtility.hotControl==traceDragControl)GUIUtility.hotControl=0;
            traceDragging=null;traceDragIndex=-1;
        }
        private void DrawPhotoTrace(Rect imageRect,bool inside)
        {
            if(photoEditMode!=4||!photoOnly||imageTex==null)return;
            var draft=CurrentPhotoTrace(false);var e=Event.current;
            int control=GUIUtility.GetControlID("PhotoTraceEditor".GetHashCode(),FocusType.Keyboard);
            Func<Vector2,Vector2> gui=p=>PhotoContourGeometry.ToGui(p,imageRect,imageTex.width,imageTex.height);
            int hit=-1;float nearest=9;
            if(draft!=null)
            {
                Handles.BeginGUI();var old=Handles.color;Handles.color=new Color(1,.6f,.1f);
                var line=draft.vertices.Select(gui).Select(p=>new Vector3(p.x,p.y,0)).ToList();
                if(draft.ready&&PhotoTraceSampler.Closed(photoEditRegion)&&line.Count>1)line.Add(line[0]);
                if(line.Count>1)Handles.DrawAAPolyLine(3,line.ToArray());
                if(!draft.ready&&line.Count>0&&inside)Handles.DrawDottedLine(line[line.Count-1],e.mousePosition,4);
                Handles.color=old;Handles.EndGUI();
                for(int i=0;i<draft.vertices.Count;i++)
                {
                    var p=gui(draft.vertices[i]);EditorGUI.DrawRect(new Rect(p-Vector2.one*4,Vector2.one*8),new Color(1,.6f,.1f));
                    float distance=Vector2.Distance(p,e.mousePosition);if(distance<nearest){hit=i;nearest=distance;}
                }
                string error;var preview=PreviewPhotoTrace(out error);
                if(preview!=null)foreach(var p in preview){var position=gui(new Vector2(p.x,p.y));EditorGUI.DrawRect(new Rect(position-Vector2.one*2.5f,Vector2.one*5),new Color(1,.25f,1));}
            }
            if(e.type==EventType.KeyDown&&!EditorGUIUtility.editingTextField&&(inside||GUIUtility.keyboardControl==control))
            {
                if(e.keyCode==KeyCode.Escape){if(traceDragging!=null)EndPhotoTraceDrag(true);else ClearCurrentPhotoTrace();e.Use();Repaint();}
                else if(e.keyCode==KeyCode.Backspace){EndPhotoTraceDrag(false);RemoveLastPhotoTraceVertex();e.Use();}
                else if(e.keyCode==KeyCode.Return||e.keyCode==KeyCode.KeypadEnter){EndPhotoTraceDrag(false);FinishPhotoTrace();e.Use();}
            }
            if(e.type==EventType.MouseDown&&inside&&imageRect.Contains(e.mousePosition)&&GUIUtility.hotControl==0)
            {
                EditorGUIUtility.editingTextField=false;GUIUtility.keyboardControl=control;
                if(e.button==1){RemoveLastPhotoTraceVertex();e.Use();}
                else if(e.button==0&&!e.alt)
                {
                    if(e.clickCount>=2){FinishPhotoTrace();e.Use();}
                    else if(hit>=0)
                    {
                        traceDragging=draft;traceDragIndex=hit;traceDragOriginal=draft.vertices[hit];traceDragControl=control;
                        Undo.IncrementCurrentGroup();traceUndoGroup=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("调整轮廓草稿");Undo.RecordObject(this,"调整轮廓草稿");
                        GUIUtility.hotControl=control;e.Use();
                    }
                    else if(draft==null||!draft.ready)
                    {
                        Undo.RecordObject(this,"添加轮廓折点");draft=CurrentPhotoTrace(true);
                        var p=PhotoContourGeometry.FromGui(e.mousePosition,imageRect,imageTex.width,imageTex.height);
                        draft.vertices.Add(new Vector2(Mathf.Clamp01(p.x),Mathf.Clamp01(p.y)));
                        if(photoEditRegion==6)draft.ready=true;e.Use();
                    }
                    Repaint();
                }
            }
            if(traceDragging!=null&&GUIUtility.hotControl==traceDragControl)
            {
                if(e.type==EventType.MouseDrag)
                {
                    var p=PhotoContourGeometry.FromGui(e.mousePosition,imageRect,imageTex.width,imageTex.height);Undo.RecordObject(this,"调整轮廓草稿");
                    traceDragging.vertices[traceDragIndex]=new Vector2(Mathf.Clamp01(p.x),Mathf.Clamp01(p.y));e.Use();Repaint();
                }
                else if(e.type==EventType.MouseUp&&e.button==0){EndPhotoTraceDrag(false);e.Use();Repaint();}
            }
        }
    }
}
