using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    // These are editing handles over semantic observations, not extra observations.
    public static class PhotoContourGeometry
    {
        public static readonly string[][] Groups = {
            Eye("left"), Eye("right"), Brow("left"), Brow("right"),
            new[]{"mouth_left","mouth_upper_left","mouth_upper_center","mouth_upper_right","mouth_right","mouth_lower_right","mouth_lower_center","mouth_lower_left"},
            new[]{"left_face_cheek","left_face_lower_cheek","left_face_jaw","chin","right_face_jaw","right_face_lower_cheek","right_face_cheek"}
        };
        public static readonly string[] Labels={"画面左眼","画面右眼","画面左眉","画面右眉","嘴部","脸廓"};
        private static string[] Eye(string side){return new[]{"outer","upper_outer","upper","upper_inner","inner","lower_inner","lower","lower_outer"}.Select(x=>side+"_eye_"+x).ToArray();}
        private static string[] Brow(string side){return new[]{"outer","outer_mid","middle","inner_mid","inner"}.Select(x=>side+"_brow_"+x).ToArray();}
        public static Vector2 ToGui(Vector2 p,Rect r,int w,int h){return new Vector2(r.x+(p.x*(w-1)+.5f)/w*r.width,r.y+(p.y*(h-1)+.5f)/h*r.height);}
        public static Vector2 FromGui(Vector2 p,Rect r,int w,int h){return new Vector2(((p.x-r.x)/r.width*w-.5f)/(w-1),((p.y-r.y)/r.height*h-.5f)/(h-1));}
        public static Rect Bounds(Vector2[] points){return Rect.MinMaxRect(points.Min(p=>p.x),points.Min(p=>p.y),points.Max(p=>p.x),points.Max(p=>p.y));}
        public static Vector2[] Translate(Vector2[] points,Vector2 delta)
        {
            var b=Bounds(points);delta.x=Mathf.Clamp(delta.x,-b.xMin,1-b.xMax);delta.y=Mathf.Clamp(delta.y,-b.yMin,1-b.yMax);
            return points.Select(p=>p+delta).ToArray();
        }
        public static Vector2[] Scale(Vector2[] points,Vector2 anchor,Vector2 corner,Vector2 delta)
        {
            var end=corner+delta;var span=corner-anchor;
            end.x=Mathf.Clamp(end.x,0,1);end.y=Mathf.Clamp(end.y,0,1);
            float sx=Mathf.Abs(span.x)<.00001f?1:Mathf.Max(.05f,(end.x-anchor.x)/span.x);
            float sy=Mathf.Abs(span.y)<.00001f?1:Mathf.Max(.05f,(end.y-anchor.y)/span.y);
            return points.Select(p=>new Vector2(Mathf.Clamp01(anchor.x+(p.x-anchor.x)*sx),Mathf.Clamp01(anchor.y+(p.y-anchor.y)*sy))).ToArray();
        }
    }

    public partial class ModelCardFitWindow
    {
        [SerializeField] private int photoEditMode=4,photoEditRegion;
        [NonSerialized] private string observedAnnotationJson;
        [NonSerialized] private Rect photoEditRect;
        [NonSerialized] private string dragAnnotation;
        [NonSerialized] private string[] dragIds;
        [NonSerialized] private Vector2[] dragPoints;
        [NonSerialized] private Vector2 dragStart,dragAnchor,dragCorner;
        [NonSerialized] private bool dragScale,dragChanged;
        [NonSerialized] private int dragControl,dragUndo;

        private void OnLostFocus(){EndPhotoDrag(false);EndPhotoPan();EndPhotoTraceDrag(false);}
        private void PhotoUndoRedo()
        {
            EndPhotoDrag(false);EndPhotoTraceDrag(false);Repaint();
            // An Undo on another object (including applying this fit) must not discard the result.
            if(JsonUtility.ToJson(photoAnnotation)!=observedAnnotationJson){RebuildPhotoLandmarks();ClearFitResult();Repaint();}
        }
        private void EndPhotoDrag(bool cancel)
        {
            if(dragIds==null||dragIds.Length==0)return;
            if(cancel&&!string.IsNullOrEmpty(dragAnnotation)){photoAnnotation=JsonUtility.FromJson<PhotoAnnotation>(dragAnnotation);RebuildPhotoLandmarks();}
            Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(dragUndo);
            if(GUIUtility.hotControl==dragControl)GUIUtility.hotControl=0;
            dragIds=null;dragPoints=null;dragAnnotation=null;dragChanged=false;
        }
        private void DrawPhotoContours(Rect rect,bool canStartDrag=true)
        {
            if(!photoOnly||photoTemplate==null||imageTex==null)return;
            photoEditRect=rect;
            var e=Event.current;int control=GUIUtility.GetControlID("PhotoContourDrag".GetHashCode(),FocusType.Passive);
            Func<string,PhotoMark> mark=id=>photoAnnotation.points.Find(p=>p.id==id&&p.state=="marked");
            Func<PhotoMark,Vector2> gui=p=>PhotoContourGeometry.ToGui(new Vector2(p.x,p.y),rect,imageTex.width,imageTex.height);
            string[] hit=null;float nearest=9;bool scale=false;Vector2 anchor=Vector2.zero,corner=Vector2.zero;
            Handles.BeginGUI();var oldColor=Handles.color;Handles.color=new Color(.1f,1f,.85f,.95f);
            for(int g=0;g<PhotoContourGeometry.Groups.Length;g++)
            {
                var ids=PhotoDetailLandmarks.OrderedRegion(g,true).Where(id=>mark(id)!=null).ToArray();bool closed=g==0||g==1||g==4;
                for(int i=0;i<ids.Length-(closed?0:1);i++)
                {
                    var a=mark(ids[i]);var b=mark(ids[(i+1)%ids.Length]);if(a==null||b==null)continue;
                    var ga=gui(a);var gb=gui(b);Handles.DrawAAPolyLine(2f,ga,gb);
                    if(photoEditMode==0){float d=HandleUtility.DistancePointToLineSegment(e.mousePosition,ga,gb);if(d<nearest){nearest=d;hit=new[]{a.id,b.id};}}
                }
            }
            if(photoEditMode==0)
            {
                // A control point always takes precedence over its adjacent segments.
                float pointDistance=9;
                foreach(var p in photoAnnotation.points.Where(p=>p.state=="marked")){float d=Vector2.Distance(e.mousePosition,gui(p));if(d<pointDistance){pointDistance=d;hit=new[]{p.id};}}
            }
            if(photoEditMode==1&&photoEditRegion<PhotoContourGeometry.Groups.Length)
            {
                var points=PhotoDetailLandmarks.OrderedRegion(photoEditRegion,true).Select(mark).Where(p=>p!=null).ToArray();
                if(points.Length>=2)
                {
                    var b=PhotoContourGeometry.Bounds(points.Select(p=>new Vector2(p.x,p.y)).ToArray());
                    var gb=Rect.MinMaxRect(gui(new PhotoMark{x=b.xMin,y=b.yMin}).x,gui(new PhotoMark{x=b.xMin,y=b.yMin}).y,gui(new PhotoMark{x=b.xMax,y=b.yMax}).x,gui(new PhotoMark{x=b.xMax,y=b.yMax}).y);
                    Handles.DrawSolidRectangleWithOutline(gb,new Color(0,1,.8f,.08f),Color.cyan);
                    if(gb.Contains(e.mousePosition))hit=points.Select(p=>p.id).ToArray();
                    foreach(var c in new[]{new Vector2(b.xMin,b.yMin),new Vector2(b.xMax,b.yMin),new Vector2(b.xMax,b.yMax),new Vector2(b.xMin,b.yMax)})
                    {
                        var cg=PhotoContourGeometry.ToGui(c,rect,imageTex.width,imageTex.height);var handle=new Rect(cg-Vector2.one*5,Vector2.one*10);EditorGUI.DrawRect(handle,Color.yellow);
                        if(handle.Contains(e.mousePosition)){hit=points.Select(p=>p.id).ToArray();scale=true;corner=c;anchor=b.center*2-c;}
                    }
                }
            }
            Handles.color=oldColor;Handles.EndGUI();
            if(e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape&&dragIds!=null){EndPhotoDrag(true);e.Use();Repaint();return;}
            if(e.type==EventType.MouseDown&&e.button==0&&canStartDrag&&hit!=null&&rect.Contains(e.mousePosition)&&GUIUtility.hotControl==0)
            {
                if(hit.Length==1)activePhotoPoint=Array.FindIndex(photoTemplate.points,p=>p.id==hit[0]);
                dragIds=hit;dragPoints=hit.Select(id=>new Vector2(mark(id).x,mark(id).y)).ToArray();dragAnnotation=JsonUtility.ToJson(photoAnnotation);
                dragStart=PhotoContourGeometry.FromGui(e.mousePosition,rect,imageTex.width,imageTex.height);dragScale=scale;dragAnchor=anchor;dragCorner=corner;dragChanged=false;
                Undo.IncrementCurrentGroup();dragUndo=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("调整图片轮廓");Undo.RecordObject(this,"调整图片轮廓");
                dragControl=control;GUIUtility.hotControl=control;e.Use();
            }
            if(dragIds!=null&&GUIUtility.hotControl==dragControl)
            {
                if(e.type==EventType.MouseDrag)
                {
                    Vector2 delta=PhotoContourGeometry.FromGui(e.mousePosition,rect,imageTex.width,imageTex.height)-dragStart;
                    var moved=dragScale?PhotoContourGeometry.Scale(dragPoints,dragAnchor,dragCorner,delta):PhotoContourGeometry.Translate(dragPoints,delta);
                    // Record every frame into the same Undo group, including long drags.
                    Undo.RecordObject(this,"调整图片轮廓");
                    for(int i=0;i<dragIds.Length;i++)photoAnnotation.Set(dragIds[i],moved[i].x,moved[i].y,"marked");
                    if(!dragChanged){ClearFitResult();dragChanged=true;}RebuildPhotoLandmarks();e.Use();Repaint();
                }
                else if(e.type==EventType.MouseUp&&e.button==0){EndPhotoDrag(false);e.Use();Repaint();}
            }
        }
    }
}
