using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VrcModelCard
{
    // Curves remain approximate polylines through reviewed model anchors, not mesh silhouettes.
    public sealed class PhotoContourObjective
    {
        public const string Version="balanced_ordered_contours_v1";
        public const int Samples=12;
        private sealed class Region
        {
            public int id;
            public int[][] paths;
            public Vector2[][] targets;
            public int[] manual;
            public Vector2[] observed;
        }
        private struct Pair { public Vector2 source,target; public float weight; }
        private readonly List<Region> regions=new List<Region>();
        private readonly LandmarkSet model=new LandmarkSet();
        private readonly Dictionary<string,int> indices=new Dictionary<string,int>();
        private readonly float pixelScale,sigma;
        private readonly bool roll;
        public float DataLoss {get;private set;}
        public float RmsePixels {get;private set;}
        public FitNormalizer Normalizer {get;private set;}
        public Vector2[] ModelSamples {get;private set;}
        public Vector2[] ImageSamples {get;private set;}
        public float[] SampleWeights {get;private set;}
        public int ContourRegions {get{return regions.Count(r=>r.paths!=null);}}

        public static int RegionOf(string id)
        {
            for(int g=0;g<7;g++)if(PhotoDetailLandmarks.OrderedRegion(g,true).Contains(id))return g;
            return 7;
        }
        public static LandmarkSet CandidateLandmarks(LandmarkSet source)
        {
            var result=new LandmarkSet();
            for(int i=0;i<source.Count;i++)if(!PhotoDetailLandmarks.IsDetail(source.renderAnchors[i].semanticId))
            {result.renderAnchors.Add(source.renderAnchors[i]);result.imagePoints.Add(source.imagePoints[i]);}
            return result.Count>=4?result:source;
        }
        private static Vector2[] Resample(Vector2[] points)
        {return Enumerable.Range(0,Samples).Select(i=>PhotoTraceSampler.SampleArc(points,i/(Samples-1f))).ToArray();}
        public static int[] OrderedMatch(Vector2[] source,Vector2[] target)
        {
            // Dynamic programming: same arc, nondecreasing correspondence, at most 3/11 arc sliding.
            int n=source.Length,m=target.Length;var cost=new float[n,m];var prev=new int[n,m];
            for(int i=0;i<n;i++)for(int j=0;j<m;j++)
            {
                cost[i,j]=float.PositiveInfinity;prev[i,j]=-1;
                if(Mathf.Abs(i/(n-1f)-j/(m-1f))>3.01f/(Samples-1))continue;
                if((i==0&&j!=0)||(i==n-1&&j!=m-1))continue;
                float d=(source[i]-target[j]).sqrMagnitude;
                if(i==0){cost[i,j]=d;continue;}
                for(int k=0;k<=j;k++)if(cost[i-1,k]+d<cost[i,j]){cost[i,j]=cost[i-1,k]+d;prev[i,j]=k;}
            }
            var match=new int[n];int end=m-1;
            for(int i=n-1;i>=0;i--){if(end<0)throw new InvalidOperationException("轮廓有序对应失败。");match[i]=end;end=prev[i,end];}
            return match;
        }
        private static Vector2[][] Split(int region,Vector2[] path)
        {
            if(region==2||region==3)return new[]{path};
            int half=region==5?(path.Length-1)/2:path.Length/2;
            return new[]{path.Take(half+1).ToArray(),region==5?path.Skip(half).ToArray():path.Skip(half).Concat(new[]{path[0]}).ToArray()};
        }
        private static Vector2[] Walk(Vector2[] raw,int start,int end,int step)
        {
            var path=new List<Vector2>{raw[start]};int i=start;
            while(i!=end){i=(i+step+raw.Length)%raw.Length;path.Add(raw[i]);}
            return path.ToArray();
        }
        private static Vector2[][] OriginalPaths(PhotoTraceRecord trace,Vector2[] basic)
        {
            var raw=trace.sourceVertices;
            Func<Vector2,int> nearest=p=>Enumerable.Range(0,raw.Length).OrderBy(i=>(raw[i]-p).sqrMagnitude).First();
            if(trace.region==2||trace.region==3)return new[]{(raw[0]-basic[0]).sqrMagnitude<(raw[raw.Length-1]-basic[0]).sqrMagnitude?raw:raw.Reverse().ToArray()};
            if(trace.region==5)
            {
                if(raw[0].x>raw[raw.Length-1].x)raw=raw.Reverse().ToArray();
                int chin=Enumerable.Range(0,raw.Length).OrderBy(i=>(raw[i]-basic[3]).sqrMagnitude).First();
                return new[]{raw.Take(chin+1).ToArray(),raw.Skip(chin).ToArray()};
            }
            int first=nearest(basic[0]),middle=nearest(basic[4]);
            var forward=Walk(raw,first,middle,1);var backward=Walk(raw,first,middle,-1);
            // Choose the path containing the sampled upper-arc midpoint, independent of tilt/winding.
            Func<Vector2[],float> distance=p=>p.Min(v=>(v-basic[2]).sqrMagnitude);
            bool upForward=distance(forward)<distance(backward);
            return new[]{upForward?forward:backward,(upForward?backward:forward).Reverse().ToArray()};
        }
        public PhotoContourObjective(LandmarkSet observations,FitSettings settings,int width,int height)
        {
            pixelScale=settings.photoPixelScale;sigma=settings.photoNoisePixels;roll=settings.fitImageRoll;
            if(pixelScale<=0||sigma<=0)throw new InvalidOperationException("图片残差尺度无效。");
            var renderer=observations.renderAnchors[0].smr;
            foreach(var p in settings.photoTemplate.points)
            {
                indices[p.id]=model.renderAnchors.Count;
                model.renderAnchors.Add(new LandmarkSet.Anchor{smr=renderer,semanticId=p.id,vertexIndex=p.vertices[0],vertexIndices=p.vertices,vertexWeights=p.weights});model.imagePoints.Add(Vector2.zero);
            }
            var annotation=settings.photoAnnotation;
            var marked=annotation.points.Where(p=>p.state=="marked").ToDictionary(p=>p.id);
            Func<Vector2,Vector2> pixel=p=>new Vector2(p.x*(width-1),(1-p.y)*(height-1));
            for(int g=0;g<8;g++)
            {
                var names=observations.renderAnchors.Select(a=>a.semanticId).Where(id=>RegionOf(id)==g).ToArray();if(names.Length==0)continue;
                var region=new Region{id=g};
                var trace=annotation.contourTraces==null?null:annotation.contourTraces.LastOrDefault(t=>t.region==g&&t.imageSha256==annotation.imageSha256);
                var basic=g<6?PhotoDetailLandmarks.OrderedRegion(g,false):new string[0];
                bool complete=basic.Length>0&&basic.All(id=>marked.ContainsKey(id)&&indices.ContainsKey(id));
                if(trace!=null&&complete)
                {
                    // Changed/deleted observations take precedence over historical source strokes.
                    var ordered=PhotoDetailLandmarks.OrderedRegion(g,true).Where(marked.ContainsKey).ToArray();
                    var current=ordered.Select(id=>new Vector2(marked[id].x,marked[id].y)).ToArray();
                    var paths=Split(g,current);
                    bool unchanged=trace.sampledPoints!=null&&trace.sampledPoints.Length==ordered.Length&&trace.sampledPoints.All(p=>marked.ContainsKey(p.id)&&Mathf.Abs(marked[p.id].x-p.x)<1e-6f&&Mathf.Abs(marked[p.id].y-p.y)<1e-6f);
                    if(unchanged&&trace.sourceVertices!=null&&trace.sourceVertices.Length>=PhotoTraceSampler.Minimum(g))
                        paths=OriginalPaths(trace,basic.Select(id=>new Vector2(marked[id].x,marked[id].y)).ToArray());
                    // Split edited paths at named corners/chin, not half the number of surviving detail points.
                    else
                    {
                        if(g!=2&&g!=3){int mid=Array.IndexOf(ordered,basic[g==5?3:4]);paths=new[]{current.Take(mid+1).ToArray(),g==5?current.Skip(mid).ToArray():current.Skip(mid).Concat(new[]{current[0]}).ToArray()};}
                    }
                    int half=g==5?3:4;
                    region.paths=(g==2||g==3)?new[]{basic.Select(id=>indices[id]).ToArray()}:new[]{basic.Take(half+1).Select(id=>indices[id]).ToArray(),(g==5?basic.Skip(half):basic.Skip(half).Concat(new[]{basic[0]})).Select(id=>indices[id]).ToArray()};
                    region.targets=paths.Select(p=>Resample(p.Select(pixel).ToArray())).ToArray();
                }
                else
                {
                    // Untraced points retain their explicit correspondence; each region has equal total mass.
                    region.manual=names.Select(id=>indices[id]).ToArray();
                    region.observed=names.Select(id=>pixel(new Vector2(marked[id].x,marked[id].y))).ToArray();
                }
                regions.Add(region);
            }
            if(regions.Count<2)throw new InvalidOperationException("至少需要两个可见部位才能对齐拟合。");
        }
        private List<Pair> Pairs(Vector2[] projected,FitNormalizer n,bool match)
        {
            var result=new List<Pair>();
            foreach(var r in regions)
            {
                if(r.paths==null){for(int i=0;i<r.manual.Length;i++)result.Add(new Pair{source=projected[r.manual[i]],target=r.observed[i],weight=1f/(regions.Count*r.manual.Length)});continue;}
                for(int p=0;p<r.paths.Length;p++)
                {
                    var source=Resample(r.paths[p].Select(i=>projected[i]).ToArray());var target=r.targets[p];
                    var aligned=source.Select(n.Apply).ToArray();
                    var forward=match?OrderedMatch(aligned,target):Enumerable.Range(0,Samples).ToArray();
                    var reverse=match?OrderedMatch(target,aligned):Enumerable.Range(0,Samples).ToArray();
                    float weight=1f/(regions.Count*r.paths.Length*Samples*2);
                    for(int i=0;i<Samples;i++){result.Add(new Pair{source=source[i],target=target[forward[i]],weight=weight});result.Add(new Pair{source=source[reverse[i]],target=target[i],weight=weight});}
                }
            }
            return result;
        }
        private FitNormalizer Align(List<Pair> pairs)
        {
            Vector2 source=Vector2.zero,target=Vector2.zero;float mass=pairs.Sum(p=>p.weight);
            foreach(var p in pairs){source+=p.source*(p.weight/mass);target+=p.target*(p.weight/mass);}
            float dot=0,cross=0,denom=0;
            foreach(var p in pairs){var a=p.source-source;var b=p.target-target;dot+=p.weight*Vector2.Dot(a,b);cross+=p.weight*(a.x*b.y-a.y*b.x);denom+=p.weight*a.sqrMagnitude;}
            if(denom<1e-8f||dot<=0)throw new InvalidOperationException("轮廓对齐退化，请检查左右及可见部位。");
            float angle=roll?Mathf.Clamp(Mathf.Atan2(cross,dot)*Mathf.Rad2Deg,-25,25):0;
            float scale=(dot*Mathf.Cos(angle*Mathf.Deg2Rad)+cross*Mathf.Sin(angle*Mathf.Deg2Rad))/denom;
            return new FitNormalizer{scale=scale,rotationDegrees=angle,offset=target-FitNormalizer.Rotate(source,angle)*scale};
        }
        public float Evaluate(Camera camera,int width,int height)
        {
            var projected=FittingOptimizer.ProjectAnchors(model,camera,width,height,new FitNormalizer{scale=1});
            var n=Align(Pairs(projected,new FitNormalizer{scale=1},false));
            for(int i=0;i<3;i++)n=Align(Pairs(projected,n,true));
            var pairs=Pairs(projected,n,true);double loss=0,squares=0;
            foreach(var pair in pairs)
            {
                float d=(n.Apply(pair.source)-pair.target).magnitude*pixelScale;
                float z=d/sigma,q=Mathf.Min(z,2);loss+=pair.weight*(.5*q*q+2*Mathf.Max(z-2,0));squares+=pair.weight*d*d;
            }
            Normalizer=n;DataLoss=(float)loss;RmsePixels=(float)Math.Sqrt(squares/2);
            ModelSamples=pairs.Select(p=>n.Apply(p.source)).ToArray();ImageSamples=pairs.Select(p=>p.target).ToArray();
            SampleWeights=pairs.Select(p=>p.weight).ToArray();
            return DataLoss;
        }
    }
}
