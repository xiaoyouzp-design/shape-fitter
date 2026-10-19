using System;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public partial class ModelCardFitWindow
    {
        [SerializeField] private float photoImageZoom=1;
        [SerializeField] private Vector2 photoImageCenter=new Vector2(.5f,.5f);
        [NonSerialized] private Rect photoViewportRect;
        [NonSerialized] private bool photoImagePanning;
        [NonSerialized] private int photoPanControl;
        [NonSerialized] private Vector2 photoPanStart,photoPanCenter;

        private void EndPhotoPan()
        {
            if(photoImagePanning&&GUIUtility.hotControl==photoPanControl)GUIUtility.hotControl=0;
            photoImagePanning=false;
        }
        private void ResetPhotoView(){EndPhotoPan();photoImageZoom=1;photoImageCenter=new Vector2(.5f,.5f);}
        private void DrawPhotoViewControls()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("图片缩放",GUILayout.Width(56));
            photoImageZoom=GUILayout.HorizontalSlider(photoImageZoom,1,8);
            EditorGUILayout.LabelField((photoImageZoom*100).ToString("0")+"%",GUILayout.Width(48));
            if(GUILayout.Button("适应窗口",GUILayout.Width(76)))ResetPhotoView();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("图内滚轮缩放（以鼠标为中心）；中键或 Alt＋左键拖动平移。100% 为适应窗口。",EditorStyles.wordWrappedMiniLabel);
            if(photoOnly)
            {
                bool hasProjection=lastResult!=null&&lastResult.fittedLandmarks!=null&&lastResult.landmarkNames!=null;
                using(new EditorGUI.DisabledScope(!hasProjection))
                    showProjectionOverlay=EditorGUILayout.ToggleLeft(new GUIContent("显示拟合偏差（黄点 / 黄线）","黄点是模型投影，不是新增标注；黄线连接模型投影与人工标注。"),showProjectionOverlay);
                EditorGUILayout.LabelField(hasProjection&&showProjectionOverlay
                    ? "青点：图片标注；黄点：模型拟合投影；黄线：两者偏差。黄点不可拖动。"
                    : "青点：图片标注。"+(hasProjection?"拟合投影已隐藏，不影响拟合结果。":"拟合后可开启偏差对照。"),EditorStyles.wordWrappedMiniLabel);
            }
        }
        private Vector2 ClampPhotoViewCenter(Vector2 size,Vector2 viewport)
        {
            float halfX=Mathf.Min(.5f,viewport.x/(2*Mathf.Max(1,size.x)));
            float halfY=Mathf.Min(.5f,viewport.y/(2*Mathf.Max(1,size.y)));
            return new Vector2(Mathf.Clamp(photoImageCenter.x,halfX,1-halfX),Mathf.Clamp(photoImageCenter.y,halfY,1-halfY));
        }
        private void DrawPhotoImageViewport(Rect viewport)
        {
            photoViewportRect=viewport;
            GUI.BeginGroup(viewport);
            try
            {
                var visible=new Rect(Vector2.zero,viewport.size);
                EditorGUI.DrawRect(visible,new Color(.12f,.12f,.12f));
                var fit=GetTextureRect(visible,imageTex.width,imageTex.height);
                photoImageZoom=Mathf.Clamp(photoImageZoom,1,8);
                var size=fit.size*photoImageZoom;
                photoImageCenter=ClampPhotoViewCenter(size,visible.size);
                var rect=new Rect(visible.center-Vector2.Scale(photoImageCenter,size),size);
                var e=Event.current;int control=GUIUtility.GetControlID("PhotoImagePan".GetHashCode(),FocusType.Passive);
                bool inside=visible.Contains(e.mousePosition);
                if(e.type==EventType.ScrollWheel&&inside)
                {
                    // Do not change the drag coordinate frame halfway through a landmark edit.
                    if(GUIUtility.hotControl==0)
                    {
                        var uv=new Vector2((e.mousePosition.x-rect.x)/size.x,(e.mousePosition.y-rect.y)/size.y);
                        photoImageZoom=Mathf.Clamp(photoImageZoom*Mathf.Exp(-e.delta.y*.09f),1,8);
                        size=fit.size*photoImageZoom;
                        var offset=e.mousePosition-visible.center;
                        photoImageCenter=uv-new Vector2(offset.x/size.x,offset.y/size.y);
                        photoImageCenter=ClampPhotoViewCenter(size,visible.size);
                    }
                    e.Use();Repaint();
                }
                if(e.type==EventType.MouseDown&&inside&&(e.button==2||(e.button==0&&e.alt))&&GUIUtility.hotControl==0)
                {
                    photoPanControl=control;GUIUtility.hotControl=control;photoImagePanning=true;
                    photoPanStart=e.mousePosition;photoPanCenter=photoImageCenter;e.Use();
                }
                if(photoImagePanning&&GUIUtility.hotControl==photoPanControl)
                {
                    if(e.type==EventType.MouseDrag)
                    {
                        var delta=e.mousePosition-photoPanStart;photoImageCenter=photoPanCenter-new Vector2(delta.x/size.x,delta.y/size.y);
                        photoImageCenter=ClampPhotoViewCenter(size,visible.size);e.Use();Repaint();
                    }
                    else if(e.type==EventType.MouseUp){EndPhotoPan();e.Use();}
                    else if(e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape){photoImageCenter=photoPanCenter;EndPhotoPan();e.Use();Repaint();}
                }
                rect=new Rect(visible.center-Vector2.Scale(photoImageCenter,size),size);
                GUI.DrawTexture(rect,imageTex,ScaleMode.StretchToFill);
                DrawPhotoTrace(rect,inside);
                HandlePhotoPointDeletion(rect,inside);
                DrawPhotoContours(rect,inside);
                if(inside&&(!photoOnly||photoEditMode==2))HandleClick(rect,imageTex.width,imageTex.height,true,e);
                DrawLandmarksOnPane(rect,imageTex.width,imageTex.height,false);
            }
            finally{GUI.EndGroup();}
        }
    }
}
