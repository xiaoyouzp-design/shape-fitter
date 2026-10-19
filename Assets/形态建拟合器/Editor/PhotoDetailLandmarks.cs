using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VrcModelCard
{
    // Optional observations with explicit, approximate model-side correspondences.
    // Expanding a template NEVER creates or interpolates photo observations.
    public static class PhotoDetailLandmarks
    {
        public const string SemanticId="face-semantic-80-v1";
        public static string MidId(string a,string b){return "detail__"+a+"__"+b;}
        public static bool IsDetail(string id){return id!=null&&id.StartsWith("detail__",StringComparison.Ordinal);}
        public static PhotoLandmarkTemplate Expand(PhotoLandmarkTemplate source)
        {
            var result=JsonUtility.FromJson<PhotoLandmarkTemplate>(JsonUtility.ToJson(source));
            var points=result.points.ToList();
            for(int g=0;g<PhotoContourGeometry.Groups.Length;g++)
            {
                var ids=PhotoContourGeometry.Groups[g];bool closed=g==0||g==1||g==4;
                for(int i=0;i<ids.Length-(closed?0:1);i++)
                {
                    var a=points.First(p=>p.id==ids[i]);var b=points.First(p=>p.id==ids[(i+1)%ids.Length]);string id=MidId(a.id,b.id);
                    if(points.Any(p=>p.id==id))continue;
                    var weights=new SortedDictionary<int,float>();
                    foreach(var endpoint in new[]{a,b})for(int k=0;k<endpoint.vertices.Length;k++)
                    {int v=endpoint.vertices[k];if(!weights.ContainsKey(v))weights[v]=0;weights[v]+=.5f*endpoint.weights[k];}
                    var nonzero=weights.Where(p=>p.Value>0).ToArray();
                    points.Add(new PhotoLandmarkDefinition{id=id,label=PhotoContourGeometry.Labels[g]+"："+ShortLabel(a.label)+"—"+ShortLabel(b.label)+"间（细分）",
                        vertices=nonzero.Select(p=>p.Key).ToArray(),weights=nonzero.Select(p=>p.Value).ToArray()});
                }
            }
            result.points=points.ToArray();result.id=SemanticId;
            result.description+="；38 个可选细分点：模型端为相邻语义锚点的等权平均，非独立解剖点；照片端须独立标记。";
            return result;
        }
        public static string ShortLabel(string label){int colon=label.IndexOf('：');return colon<0?label:label.Substring(colon+1);}
        public static string[] OrderedRegion(int region,bool detailed)
        {
            if(region==6)return new[]{"nose_tip"};
            var ids=PhotoContourGeometry.Groups[Mathf.Clamp(region,0,5)];var result=new List<string>();bool closed=region==0||region==1||region==4;
            for(int i=0;i<ids.Length;i++){result.Add(ids[i]);if(detailed&&(closed||i<ids.Length-1))result.Add(MidId(ids[i],ids[(i+1)%ids.Length]));}
            return result.ToArray();
        }
    }
}
