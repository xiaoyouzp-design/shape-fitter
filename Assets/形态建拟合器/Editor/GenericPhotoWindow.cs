using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    public partial class ModelCardFitWindow
    {
        [SerializeField] private TextAsset photoProfileAsset;
        private CommonCoordinateProfile photoProfile;
        private bool photoCoordinatesDirty, showPhotoCoordinateOptions;
        private void OnProjectChange() { if (lastResult == null) photoCoordinatesDirty=true; Repaint(); }

        private void DrawPhotoModelBinding()
        {
            EditorGUILayout.HelpBox(templateRenderer == null ? templateStatus : "已绑定模型坐标："+templateStatus,
                templateRenderer == null ? MessageType.Warning : MessageType.Info);
            if(templateRenderer == null && root != null)
                if(GUILayout.Button("为此模型构建坐标…")) OpenPhotoCoordinateBuilder();
            showPhotoCoordinateOptions=EditorGUILayout.Foldout(showPhotoCoordinateOptions,"模型坐标设置",true);
            if(!showPhotoCoordinateOptions)return;
            var next=(TextAsset)EditorGUILayout.ObjectField("指定坐标档案（可选）",photoProfileAsset,typeof(TextAsset),false);
            if(next!=photoProfileAsset){photoProfileAsset=next;BindPhotoTemplate();}
            EditorGUILayout.LabelField("默认读取该网格最新可用档案；照片标点可跨模型复用，不需要重复标图片。",EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("重新读取坐标"))BindPhotoTemplate();
            using(new EditorGUI.DisabledScope(root==null))
                if(GUILayout.Button("检查 / 构建模型坐标…"))OpenPhotoCoordinateBuilder();
            EditorGUILayout.EndHorizontal();
            if(photoProfile!=null)EditorGUILayout.LabelField("档案",photoProfile.description+" ["+photoProfile.id+"]",EditorStyles.wordWrappedMiniLabel);
        }

        private void OpenPhotoCoordinateBuilder()
        {
            var faces=root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r=>r.sharedMesh!=null&&r.sharedMesh.blendShapeCount>0).ToArray();
            var face=templateRenderer!=null?templateRenderer:faces.Length==1?faces[0]:null;
            CommonCoordinateBuilderWindow.Open();
            var builder=EditorWindow.GetWindow<CommonCoordinateBuilderWindow>();
            // Multiple candidate meshes require an explicit choice in the builder.
            if(face!=null)builder.Configure(face,photoProfile);
        }
    }
}
