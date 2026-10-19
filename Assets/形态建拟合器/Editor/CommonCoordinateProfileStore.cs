using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class CoordinateProfileRecord
    {
        public int schema=1;
        public string modelName,meshName,createdUtc,status,validationError;
        public bool usable;
        public CommonCoordinateProfile profile;
        public CoordinateAutoResult automatic;
        [NonSerialized] public string assetPath;
    }
    public static class CommonCoordinateProfileStore
    {
        public const string Root="Assets/形态键拟合/模型坐标";
        public static string ModelName(SkinnedMeshRenderer face)
        {
            if(face==null)throw new InvalidOperationException("请选择脸部网格。");
            var animator=face.GetComponentsInParent<Animator>(true).FirstOrDefault();
            if(animator!=null)return animator.gameObject.name;
            var prefab=PrefabUtility.GetNearestPrefabInstanceRoot(face.gameObject);
            if(prefab!=null)return prefab.name;
            return face.transform.parent!=null?face.transform.parent.name:face.gameObject.name;
        }
        public static string SafeName(string value,int limit=40)
        {
            var invalid=Path.GetInvalidFileNameChars().Concat(new[]{'/','\\',':','*','?','"','<','>','|'}).ToArray();
            var text=new string((value??"").Select(c=>char.IsControl(c)||invalid.Contains(c)?'_':c).ToArray()).Trim(' ','.');
            if(text.Length==0)text="未命名模型";
            if(text.Length>limit)text=text.Substring(0,limit);
            var stem=text.Split('.')[0].ToUpperInvariant();
            if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem))text="模型_"+text;
            return text;
        }
        private static void Folder(string path)
        {
            if(AssetDatabase.IsValidFolder(path))return;
            var parent=Path.GetDirectoryName(path).Replace('\\','/');Folder(parent);AssetDatabase.CreateFolder(parent,Path.GetFileName(path));
        }
        public static string Fingerprint(CommonCoordinateProfile profile)
        {return profile.vertexCount+"|"+profile.blendShapeCount+"|"+profile.topologySha256+"|"+profile.shapeNamesSha256;}
        public static CoordinateProfileRecord Save(SkinnedMeshRenderer face,CommonCoordinateProfile profile,CoordinateAutoResult automatic=null)
        {
            if(face==null||profile==null||!profile.Matches(face.sharedMesh))throw new InvalidOperationException("坐标与当前模型不匹配，无法自动归档。");
            var saved=JsonUtility.FromJson<CommonCoordinateProfile>(JsonUtility.ToJson(profile));
            bool usable=false;string status="待检查",validationError="";
            if(saved.points==null||saved.keys==null||saved.keys.Length==0||saved.points.Any(p=>p.vertices==null||p.vertices.Length==0))status="未完成";
            else if(!saved.automaticReviewRequired||saved.automaticReviewed)
            {
                try
                {
                    string hash;CommonCoordinateDefinitions.Load(out hash);saved.Validate(hash);
                    // Explicit profile preserves the legacy schema when reusing known coordinates.
                    CommonCoordinateModel.Capture(face.gameObject,hash,saved);usable=true;status="可用";
                }
                catch(Exception ex){status="校验未通过";validationError=ex.Message;}
            }
            var model=ModelName(face);var mesh=face.sharedMesh.name;var stamp=DateTimeOffset.UtcNow;
            var fingerprint=PhotoLandmarkTemplate.Hash(Encoding.UTF8.GetBytes(Fingerprint(saved))).Substring(0,10);
            string folder=Root+"/"+SafeName(model)+"_"+fingerprint;Folder(folder);
            string name=SafeName(model)+"_"+SafeName(mesh,24)+"_"+stamp.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd_HHmmss_fff")+"_"+status;
            string path=folder+"/"+name+".CoordinateArchive.json";int revision=1;
            while(File.Exists(path))path=folder+"/"+name+"_"+(revision++)+".CoordinateArchive.json";
            var record=new CoordinateProfileRecord{modelName=model,meshName=mesh,createdUtc=stamp.ToString("o"),status=status,usable=usable,validationError=validationError,profile=saved,
                automatic=automatic==null?null:JsonUtility.FromJson<CoordinateAutoResult>(JsonUtility.ToJson(automatic)),assetPath=path};
            if(record.automatic!=null)record.automatic.profile=saved;
            // CreateNew guarantees an older archive is never overwritten.
            using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(JsonUtility.ToJson(record,true));
            AssetDatabase.ImportAsset(path);return record;
        }
        public static CoordinateProfileRecord Read(string path)
        {
            var record=JsonUtility.FromJson<CoordinateProfileRecord>(File.ReadAllText(path));
            if(record==null||record.schema!=1||record.profile==null||string.IsNullOrEmpty(record.createdUtc))throw new InvalidOperationException("坐标存档无效。");
            record.assetPath=path;return record;
        }
        public static CoordinateProfileRecord[] Records()
        {
            if(!AssetDatabase.IsValidFolder(Root))return new CoordinateProfileRecord[0];
            var records=new List<CoordinateProfileRecord>();
            foreach(var path in AssetDatabase.FindAssets("t:TextAsset",new[]{Root}).Select(AssetDatabase.GUIDToAssetPath).Where(p=>p.EndsWith(".CoordinateArchive.json",StringComparison.Ordinal)))
                try{records.Add(Read(path));}catch{ /* Broken history does not disable unrelated models. */ }
            return records.OrderByDescending(r=>r.createdUtc,StringComparer.Ordinal).ThenByDescending(r=>r.assetPath,StringComparer.Ordinal).ToArray();
        }
        public static CommonCoordinateProfile[] DiscoverProfiles(string definitionHash)
        {
            var profiles=new List<CommonCoordinateProfile>();
            foreach(var path in AssetDatabase.FindAssets("t:TextAsset").Select(AssetDatabase.GUIDToAssetPath).Where(p=>p.EndsWith("CoordinateProfile.json",StringComparison.OrdinalIgnoreCase)))
                try{var p=JsonUtility.FromJson<CommonCoordinateProfile>(File.ReadAllText(path));if(p!=null)profiles.Add(p);}catch{}
            var seen=new HashSet<string>();
            foreach(var record in Records())
            {
                if(!record.usable)continue;
                try{record.profile.Validate(definitionHash);}catch{continue;}
                string key=Fingerprint(record.profile);if(!seen.Add(key))continue;
                profiles.RemoveAll(p=>Fingerprint(p)==key);profiles.Add(record.profile);
            }
            return profiles.ToArray();
        }
        public static CommonCoordinateProfile LoadProfile(string path)
        {
            if(path.EndsWith(".CoordinateArchive.json",StringComparison.OrdinalIgnoreCase))return Read(path).profile;
            return JsonUtility.FromJson<CommonCoordinateProfile>(File.ReadAllText(path));
        }
    }
}
