using System;
using System.Collections.Generic;
using System.Linq;

namespace VrcModelCard
{
    [Serializable] public class CommonCoordinateAttempt
    {
        public double[] initialWeights, weights;
        public double objective, projectedGradient;
        public int sweeps;
        public bool converged, cancelled;
    }
    [Serializable] public class CommonCoordinateFitResult
    {
        public string sourceProfile, destinationProfile, mode, lossMode, definitionSha256;
        public string scope="Current Rurune/Plum mappings only; no arbitrary VRM, photo, identity or global nonlinear guarantee.";
        public int seed, selectedStart;
        public string[] keys, lowerBoundary, upperBoundary;
        public double[] weights, referenceWeights, targetCoordinates, fittedCoordinates;
        public double priorCoefficient, detailCoefficient, featureCoefficient;
        public double objective, pointRmse, frontalPointRmse, geometryPointRmse, featureRmse, priorSse;
        public double purePointSse, pureFeatureSse, bakeMaxError;
        public bool hasTargetPoints, allDetailPoints;
        public string[] individuallyUnreachable;
        public CommonCoordinateAttempt[] attempts;
        public string unityVersion;
    }

    public static class CommonCoordinateSolver
    {
        // Primal active-set solver for a strictly convex box quadratic in x=w/100.
        // Positive native prior makes the normal matrix definite; independent
        // SciPy BVLS checks cover all shipped modes. No truth enters selection.
        public static CommonCoordinateAttempt Quadratic(double[,] a,double[] b,double[] initial,Func<bool> cancel=null)
        {
            int rows=a.GetLength(0),n=a.GetLength(1);
            if(b.Length!=rows || initial.Length!=n)throw new ArgumentException("Quadratic dimensions disagree.");
            var x=(double[])initial.Clone();var active=new int[n];
            var h=new double[n,n];var q=new double[n];
            for(int j=0;j<n;j++)
            {
                x[j]=Math.Max(0,Math.Min(1,x[j]));active[j]=x[j]==0?-1:(x[j]==1?1:0);
                for(int r=0;r<rows;r++)q[j]+=a[r,j]*b[r];
                for(int k=0;k<=j;k++){double v=0;for(int r=0;r<rows;r++)v+=a[r,j]*a[r,k];h[j,k]=h[k,j]=v;}
            }
            var result=new CommonCoordinateAttempt{initialWeights=initial.Select(v=>v*100).ToArray()};
            const double tolerance=1e-9;
            for(int iteration=0;iteration<2000;iteration++)
            {
                if(cancel!=null && cancel()){result.cancelled=true;break;}
                result.sweeps=iteration+1;
                var free=Enumerable.Range(0,n).Where(j=>active[j]==0).ToArray();
                int count=free.Length;var rhs=new double[count];var l=new double[count,count];
                for(int i=0;i<count;i++)
                {
                    int j=free[i];rhs[i]=q[j];for(int k=0;k<n;k++)if(active[k]!=0)rhs[i]-=h[j,k]*x[k];
                    for(int k=0;k<=i;k++)
                    {
                        double v=h[j,free[k]];for(int t=0;t<k;t++)v-=l[i,t]*l[k,t];
                        if(i==k){if(v<=0)throw new InvalidOperationException("数值矩阵非正定；需要正的权重先验。");l[i,k]=Math.Sqrt(v);}
                        else l[i,k]=v/l[k,k];
                    }
                }
                var u=new double[count];
                for(int i=0;i<count;i++){double v=rhs[i];for(int k=0;k<i;k++)v-=l[i,k]*u[k];u[i]=v/l[i,i];}
                for(int i=count-1;i>=0;i--){double v=u[i];for(int k=i+1;k<count;k++)v-=l[k,i]*u[k];u[i]=v/l[i,i];}
                double alpha=1;int hit=-1,side=0;
                for(int i=0;i<count;i++)
                {
                    int j=free[i];double d=u[i]-x[j];
                    if(u[i]<0 && d<0){double t=-x[j]/d;if(t<alpha){alpha=t;hit=j;side=-1;}}
                    if(u[i]>1 && d>0){double t=(1-x[j])/d;if(t<alpha){alpha=t;hit=j;side=1;}}
                }
                for(int i=0;i<count;i++){int j=free[i];x[j]=Math.Max(0,Math.Min(1,x[j]+alpha*(u[i]-x[j])));}
                if(hit>=0){x[hit]=side<0?0:1;active[hit]=side;continue;}
                double pg=0,worst=tolerance;int release=-1;
                for(int j=0;j<n;j++)
                {
                    double g=-q[j];for(int k=0;k<n;k++)g+=h[j,k]*x[k];
                    double projected=x[j]<=1e-12?Math.Min(0,g):(x[j]>=1-1e-12?Math.Max(0,g):g);
                    pg=Math.Max(pg,Math.Abs(projected));
                    double violation=active[j]<0?-g:(active[j]>0?g:0);
                    if(violation>worst){worst=violation;release=j;}
                }
                result.projectedGradient=pg;
                if(pg<tolerance){result.converged=true;break;}
                if(release<0)break;active[release]=0;
            }
            double objective=0;var gradient=new double[n];
            for(int r=0;r<rows;r++)
            {
                double e=-b[r];for(int j=0;j<n;j++)e+=a[r,j]*x[j];objective+=e*e;
                for(int j=0;j<n;j++)gradient[j]+=a[r,j]*e;
            }
            result.projectedGradient=Enumerable.Range(0,n).Max(j=>Math.Abs(x[j]<=1e-12?Math.Min(0,gradient[j]):(x[j]>=1-1e-12?Math.Max(0,gradient[j]):gradient[j])));
            result.weights=x.Select(v=>v*100).ToArray();result.objective=objective;return result;
        }

        public static void BuildObjective(CommonCoordinateModel model,CommonCoordinateDefinitions definitions,double[,] targetPoints,double[] targetValues,
            bool depth,bool allPoints,double detailCoefficient,double featureCoefficient,double prior,out double[,] a,out double[] b)
        {
            var rows=new List<double[]>();var rhs=new List<double>();int n=model.reference.Length;
            Action<double[],double> add=(row,value)=>{rows.Add(row);rhs.Add(value);};
            int dims=depth?3:2;
            if(targetPoints!=null && detailCoefficient>0)
                for(int p=0;p<model.profile.points.Length;p++)
                {
                    string id=model.profile.points[p].id;
                    if(!allPoints && IsHeldout(id))continue;
                    for(int c=0;c<dims;c++)
                    {
                        var row=new double[n];double value=targetPoints[p,c]-model.anchors[p,c];
                        for(int k=0;k<n;k++){row[k]=100*model.anchorDeltas[k,p,c];value+=model.anchorDeltas[k,p,c]*model.reference[k];}
                        add(row.Select(v=>v*detailCoefficient).ToArray(),value*detailCoefficient);
                    }
                }
            var current=definitions.Measure(model.anchors,model.Names);
            var lookup=model.Names.Select((name,i)=>new{name,i}).ToDictionary(p=>p.name,p=>p.i);
            if(featureCoefficient>0)for(int f=0;f<definitions.definitions.Length;f++)
            {
                var d=definitions.definitions[f];if(!depth && d.dimension=="depth")continue;
                var row=new double[n];double value=targetValues[f]-current[f];
                for(int k=0;k<n;k++)
                {
                    double derivative=d.terms.Sum(t=>t.coefficient*model.anchorDeltas[k,lookup[t.point],t.axis]);
                    row[k]=100*derivative;value+=derivative*model.reference[k];
                }
                add(row.Select(v=>v*featureCoefficient).ToArray(),value*featureCoefficient);
            }
            if(rows.Count==0)throw new InvalidOperationException("没有可拟合的几何观测。");
            for(int k=0;k<n;k++){var row=new double[n];row[k]=prior;add(row,prior*model.reference[k]/100);}
            a=new double[rows.Count,n];b=rhs.ToArray();for(int r=0;r<rows.Count;r++)for(int k=0;k<n;k++)a[r,k]=rows[r][k];
        }
        public static bool IsHeldout(string id)
        {
            return new[]{"upper_inner","upper_outer","lower_inner","lower_outer","inner_mid","outer_mid","lower_cheek"}.Any(s=>id.EndsWith(s,StringComparison.Ordinal));
        }
        public static CommonCoordinateFitResult Fit(CommonCoordinateModel model,CommonCoordinateDefinitions definitions,double[,] targetPoints,double[] targetValues,
            bool depth,bool allPoints,double detailCoefficient,double featureCoefficient,double prior,int seed,
            Action<int> progress=null,Func<bool> cancel=null)
        {
            if(targetValues==null || targetValues.Length!=definitions.definitions.Length || targetValues.Any(v=>!CommonCoordinateDefinitions.Finite(v)))
                throw new InvalidOperationException("目标坐标不完整或含非有限数值。");
            double[,] a;double[] b;BuildObjective(model,definitions,targetPoints,targetValues,depth,allPoints,detailCoefficient,featureCoefficient,prior,out a,out b);
            var attempts=new List<CommonCoordinateAttempt>();var random=new Random(seed);
            for(int start=0;start<5;start++)
            {
                if(progress!=null)progress(start);
                var initial=model.reference.Select(w=>w/100).ToArray();
                if(start>0)for(int k=0;k<initial.Length;k++)initial[k]=random.NextDouble();
                var attempt=Quadratic(a,b,initial,cancel);attempts.Add(attempt);if(attempt.cancelled)break;
            }
            int selected=Enumerable.Range(0,attempts.Count).OrderBy(i=>attempts[i].objective).First();
            var weights=attempts[selected].weights;var pred=model.Predict(weights);var pv=definitions.Measure(pred,model.Names);
            double e2=0,e3=0,pointSse=0,featureSse=0,priorSse=0;int count=0,featureCount=0;
            if(targetPoints!=null)for(int p=0;p<pred.GetLength(0);p++)
            {
                double p2=0,p3=0;for(int c=0;c<3;c++){double e=pred[p,c]-targetPoints[p,c];p3+=e*e;if(c<2)p2+=e*e;}
                e2+=p2;e3+=p3;count++;
                if(allPoints || !IsHeldout(model.profile.points[p].id))pointSse+=depth?p3:p2;
            }
            for(int f=0;f<pv.Length;f++)if(depth || definitions.definitions[f].dimension!="depth")
            {double e=pv[f]-targetValues[f];featureSse+=e*e;featureCount++;}
            for(int k=0;k<weights.Length;k++){double r=prior*(weights[k]-model.reference[k])/100;priorSse+=r*r;}
            var current=definitions.Measure(model.anchors,model.Names);var outside=new List<string>();
            var lookup=model.Names.Select((name,i)=>new{name,i}).ToDictionary(p=>p.name,p=>p.i);
            for(int f=0;f<pv.Length;f++)
            {
                var d=definitions.definitions[f];if(!depth && d.dimension=="depth")continue;
                double lo=current[f],hi=current[f];
                for(int k=0;k<weights.Length;k++)
                {
                    double j=d.terms.Sum(t=>t.coefficient*model.anchorDeltas[k,lookup[t.point],t.axis]);
                    double v0=-j*model.reference[k],v1=j*(100-model.reference[k]);lo+=Math.Min(v0,v1);hi+=Math.Max(v0,v1);
                }
                if(targetValues[f]<lo-1e-9 || targetValues[f]>hi+1e-9)outside.Add(d.id);
            }
            return new CommonCoordinateFitResult{destinationProfile=model.profile.id,mode=depth?"geometry_3d":"frontal_2d",
                lossMode=detailCoefficient>0 && targetPoints!=null?(featureCoefficient>0?"hybrid":"points_only"):"coordinates_only",
                keys=model.profile.keys,weights=weights,referenceWeights=model.reference,targetCoordinates=targetValues,fittedCoordinates=pv,
                priorCoefficient=prior,detailCoefficient=detailCoefficient,featureCoefficient=featureCoefficient,
                objective=attempts[selected].objective,purePointSse=pointSse,pureFeatureSse=featureSse,priorSse=priorSse,
                frontalPointRmse=count>0?100*Math.Sqrt(e2/count):-1,geometryPointRmse=count>0?100*Math.Sqrt(e3/count):-1,
                pointRmse=count>0?100*Math.Sqrt((depth?e3:e2)/count):-1,featureRmse=100*Math.Sqrt(featureSse/featureCount),
                lowerBoundary=model.profile.keys.Where((s,k)=>weights[k]<.01).ToArray(),upperBoundary=model.profile.keys.Where((s,k)=>weights[k]>99.99).ToArray(),
                individuallyUnreachable=outside.ToArray(),attempts=attempts.ToArray(),selectedStart=selected,seed=seed,
                hasTargetPoints=targetPoints!=null,allDetailPoints=allPoints,bakeMaxError=model.bakeMaxError,unityVersion=UnityEngine.Application.unityVersion};
        }
    }
}
