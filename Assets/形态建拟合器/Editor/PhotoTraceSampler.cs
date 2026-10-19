using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace VrcModelCard
{
    [Serializable] public class PhotoTraceDraft
    {
        public int region;
        public string imageSha256;
        public bool ready;
        public List<Vector2> vertices=new List<Vector2>();
    }
    [Serializable] public class PhotoTraceRecord
    {
        public int region;
        public string imageSha256,createdUtc;
        public string sampling="contour_arclength_v1";
        public bool detailed;
        public Vector2[] sourceVertices;
        public PhotoMark[] sampledPoints;
        public string note="由用户绘制的轮廓按弧长采样，非独立检测点；采样后可手工调整，当前有效观测以 points 为准。";
    }
    public static class PhotoTraceSampler
    {
        public static bool Closed(int region){return region==0||region==1||region==4;}
        public static int Minimum(int region){return region==6?1:Closed(region)?6:region==5?5:3;}
        private static float Cross(Vector2 a,Vector2 b){return a.x*b.y-a.y*b.x;}
        private static bool OnSegment(Vector2 a,Vector2 b,Vector2 p)
        {return Mathf.Abs(Cross(b-a,p-a))<.0001f&&p.x>=Mathf.Min(a.x,b.x)-.0001f&&p.x<=Mathf.Max(a.x,b.x)+.0001f&&p.y>=Mathf.Min(a.y,b.y)-.0001f&&p.y<=Mathf.Max(a.y,b.y)+.0001f;}
        private static bool Intersects(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
        {
            float x=Cross(b-a,c-a),y=Cross(b-a,d-a),z=Cross(d-c,a-c),w=Cross(d-c,b-c);
            return (x*y<0&&z*w<0)||OnSegment(a,b,c)||OnSegment(a,b,d)||OnSegment(c,d,a)||OnSegment(c,d,b);
        }
        public static Vector2 SampleArc(IList<Vector2> path,float fraction)
        {
            if(path.Count==0)throw new InvalidOperationException("轮廓为空。");
            float length=0;for(int i=1;i<path.Count;i++)length+=Vector2.Distance(path[i-1],path[i]);
            if(length<.00001f)return path[0];
            float target=Mathf.Clamp01(fraction)*length;
            for(int i=1;i<path.Count;i++){float edge=Vector2.Distance(path[i-1],path[i]);if(target<=edge)return Vector2.Lerp(path[i-1],path[i],edge<.00001f?0:target/edge);target-=edge;}
            return path[path.Count-1];
        }
        private static List<Vector2> Arc(List<Vector2> points,int start,int end,int step)
        {
            var path=new List<Vector2>{points[start]};int i=start;
            while(i!=end){i=(i+step+points.Count)%points.Count;path.Add(points[i]);}
            return path;
        }
        private static Vector2 HorizontalAxis(List<Vector2> points,bool closed)
        {
            var path=points.ToList();if(closed)path.Add(points[0]);
            // Uniform perimeter samples keep the direction independent of click density.
            var samples=Enumerable.Range(0,64).Select(i=>SampleArc(path,i/63f)).ToArray();
            var mean=samples.Aggregate(Vector2.zero,(sum,p)=>sum+p)/samples.Length;
            float xx=0,xy=0,yy=0;foreach(var p in samples){var q=p-mean;xx+=q.x*q.x;xy+=q.x*q.y;yy+=q.y*q.y;}
            float disc=Mathf.Sqrt((xx-yy)*(xx-yy)+4*xy*xy),major=(xx+yy+disc)*.5f,minor=(xx+yy-disc)*.5f;
            if(major<1||major<minor*1.5f)throw new InvalidOperationException("轮廓方向不明确。请拉长、修正轮廓，或用补点模式调整；草稿已保留。");
            float angle=.5f*Mathf.Atan2(2*xy,xx-yy);
            if(Mathf.Abs(angle)>Mathf.PI*.25f)throw new InvalidOperationException("倾斜过大，无法稳妥分配左右。请先使用较正的图片；草稿已保留。");
            return new Vector2(Mathf.Cos(angle),Mathf.Sin(angle));
        }
        private static float MeanProjection(List<Vector2> path,Vector2 axis)
        {return Enumerable.Range(0,33).Average(i=>Vector2.Dot(SampleArc(path,i/32f),axis));}

        public static PhotoMark[] Sample(int region,IList<Vector2> normalized,int width,int height,bool detailed)
        {
            if(region<0||region>6||width<2||height<2||normalized==null)throw new InvalidOperationException("图片或部位无效。");
            if(normalized.Any(p=>!CommonCoordinateDefinitions.Finite(p.x)||!CommonCoordinateDefinitions.Finite(p.y)||p.x<0||p.x>1||p.y<0||p.y>1))throw new InvalidOperationException("轮廓超出图片或坐标无效。");
            var points=new List<Vector2>();foreach(var p in normalized){var pixel=new Vector2(p.x*(width-1),p.y*(height-1));if(points.Count==0||Vector2.Distance(points[points.Count-1],pixel)>.75f)points.Add(pixel);}
            bool closed=Closed(region);if(closed&&points.Count>1&&Vector2.Distance(points[0],points[points.Count-1])<.75f)points.RemoveAt(points.Count-1);
            if(points.Count<Minimum(region))throw new InvalidOperationException("这条轮廓至少需要 "+Minimum(region)+" 个不同的折点，当前只有 "+points.Count+" 个。");
            if(region==6)
            {
                if(points.Count!=1)throw new InvalidOperationException("鼻尖只需点击一次；请重新绘制此部位。");
                return new[]{new PhotoMark{id="nose_tip",state="marked",x=points[0].x/(width-1),y=points[0].y/(height-1)}};
            }
            int edges=points.Count-(closed?0:1);
            for(int i=0;i<edges;i++)for(int j=i+1;j<edges;j++)
            {
                if(j==i+1||(closed&&i==0&&j==edges-1))continue;
                if(Intersects(points[i],points[(i+1)%points.Count],points[j],points[(j+1)%points.Count]))throw new InvalidOperationException("轮廓有交叉或折返，请拖动折点修正；草稿已保留。");
            }
            var ids=PhotoDetailLandmarks.OrderedRegion(region,detailed);var samples=new Vector2[ids.Length];
            if(closed)
            {
                var axis=HorizontalAxis(points,true);var down=new Vector2(-axis.y,axis.x);
                int left=0,right=0;for(int i=1;i<points.Count;i++){if(Vector2.Dot(points[i],axis)<Vector2.Dot(points[left],axis))left=i;if(Vector2.Dot(points[i],axis)>Vector2.Dot(points[right],axis))right=i;}
                if(Vector2.Dot(points[right]-points[left],axis)<4||points.Max(p=>Vector2.Dot(p,down))-points.Min(p=>Vector2.Dot(p,down))<2)throw new InvalidOperationException("轮廓过小或太扁，请放大图片后重新描绘。");
                var one=Arc(points,left,right,1);var two=Arc(points,left,right,-1);
                float mean1=MeanProjection(one,down),mean2=MeanProjection(two,down);
                if(Mathf.Abs(mean1-mean2)<1)throw new InvalidOperationException("无法区分上下轮廓，请检查折线；草稿已保留。");
                var upper=mean1<mean2?one:two;var lower=mean1<mean2?two:one;
                // Screen-right eye starts at its screen-right outer corner.
                if(region==1)upper.Reverse();else lower.Reverse();
                int half=ids.Length/2;
                for(int i=0;i<ids.Length;i++)samples[i]=i<=half?SampleArc(upper,(float)i/half):SampleArc(lower,(float)(i-half)/half);
            }
            else if(region==5)
            {
                if(points[0].x>points[points.Count-1].x)points.Reverse();
                var span=points[points.Count-1]-points[0];
                if(span.x<4||Mathf.Abs(span.y)>span.x)throw new InvalidOperationException("脸廓请从一侧脸颊，经下巴画到另一侧脸颊；两端应在脸的两侧。");
                var axis=span.normalized;var down=new Vector2(-axis.y,axis.x);int chin=0;
                for(int i=1;i<points.Count;i++)if(Vector2.Dot(points[i]-points[0],down)>Vector2.Dot(points[chin]-points[0],down))chin=i;
                if(chin==0||chin==points.Count-1||Vector2.Dot(points[chin]-points[0],down)<2)throw new InvalidOperationException("没有明确的下巴转折，请补充脸部下缘；草稿已保留。");
                var first=points.Take(chin+1).ToList();var second=points.Skip(chin).ToList();int half=(ids.Length-1)/2;
                for(int i=0;i<ids.Length;i++)samples[i]=i<=half?SampleArc(first,(float)i/half):SampleArc(second,(float)(i-half)/half);
            }
            else
            {
                var axis=HorizontalAxis(points,false);float start=Vector2.Dot(points[0],axis),end=Vector2.Dot(points[points.Count-1],axis);
                float extent=points.Max(p=>Vector2.Dot(p,axis))-points.Min(p=>Vector2.Dot(p,axis));
                if(extent<4||Mathf.Abs(end-start)<.75f*extent)throw new InvalidOperationException("眉毛请从一端沿中线画到另一端，不要画闭合圈。");
                if((region==2&&start>end)||(region==3&&start<end))points.Reverse();
                for(int i=0;i<ids.Length;i++)samples[i]=SampleArc(points,(float)i/(ids.Length-1));
            }
            return ids.Select((id,i)=>new PhotoMark{id=id,state="marked",x=samples[i].x/(width-1),y=samples[i].y/(height-1)}).ToArray();
        }
    }
}
