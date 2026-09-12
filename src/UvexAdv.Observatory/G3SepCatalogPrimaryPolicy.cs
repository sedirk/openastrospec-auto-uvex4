namespace UvexAdv.Observatory;

/// <summary>Match a whole SEP parent region to an independently resolved exact-ID
/// astrometric primary. A deblend knot is never a star-identity witness. This is
/// coarse photocentre measurement, not exact placement of a resolved stellar core.</summary>
public static class G3SepCatalogPrimaryPolicy
{
    public static G3ShortPositionMeasurement Measure(SepImageMeasurements image,PixelPoint primaryPrediction,
        PixelPoint companionVector,double recognitionRadius,double minimumSnr=8)
    {
        var separation=Distance(companionVector,new(0,0));
        var limit=Math.Min(recognitionRadius,separation/3);
        var companion=new PixelPoint(primaryPrediction.X+companionVector.X,primaryPrediction.Y+companionVector.Y);
        var metrics=new Dictionary<string,double> { ["sepBackend"]=1,["catalogPrimaryAssociationRadiusPixels"]=limit,
            ["catalogCompanionExpectedSeparationPixels"]=separation };
        G3ShortPositionMeasurement Reject(string code,string reason)=>new(GateResult.Unknown(code,reason,metrics),
            new(GateResult.Unknown(code,reason),null,primaryPrediction,double.PositiveInfinity,0),true,0,[]);
        if(image.Algorithm!=SepStarDetectionClient.Algorithm || image.TargetIdentityConfirmed || image.MotionAuthorized
            || image.ParentComponents is null || image.ParentComponentsTruncated)
            return Reject("G3_SEP_INVALID_COVERAGE","Complete SEP parent measurements are required.");
        if(!double.IsFinite(separation) || separation is <8 or >100 || !double.IsFinite(recognitionRadius) || recognitionRadius<=0
            || !double.IsFinite(primaryPrediction.X) || !double.IsFinite(primaryPrediction.Y)
            || !double.IsFinite(minimumSnr) || minimumSnr<=0)
            return Reject("G3_SEP_REFERENCE_INVALID","An independently bound astrometric primary and disjoint companion region are required.");
        var parents=image.ParentComponents.Where(c=>c.SaturatedFraction==0 && c.Flux>0 && c.Snr>=minimumSnr
            && c.Npix>=9 && c.RawSupportPixels>=3 && c.SepFlags==0
            && c.Bbox[0]>0 && c.Bbox[1]>0 && c.Bbox[0]+c.Bbox[2]<image.Width && c.Bbox[1]+c.Bbox[3]<image.Height
            && Distance(new(c.X,c.Y),primaryPrediction)<=limit
            && Distance(new(c.X,c.Y),companion)>2*limit).ToArray();
        metrics["sepCatalogPrimaryParents"]=parents.Length;
        if(parents.Length!=1) return Reject("G3_SEP_CATALOG_PRIMARY_UNMEASURED",
            "A unique whole SEP region has not matched the exact-ID astrometric primary; companion-only or sub-lobe coincidences are not accepted.");
        var p=parents[0];
        var residual=Distance(new(p.X,p.Y),primaryPrediction);
        // Preserve an empirical coarse uncertainty, including the offset from
        // the astrometric reference; never claim that an asymmetric photocentre
        // is the exact resolved primary or a calibrated PSF sigma.
        var spread=Math.Max(3.5,residual);
        metrics["catalogPrimaryAstrometricResidualPixels"]=residual;
        metrics["shortPositionSpreadPixels"]=spread;
        var star=new StarCandidate(new(p.X,p.Y),p.Peak,p.Flux,p.Snr,0,1-p.B/p.A,0,
            Math.Min(Math.Min(p.X,p.Y),Math.Min(image.Width-1-p.X,image.Height-1-p.Y)));
        var gate=GateResult.Pass("G3_SEP_CATALOG_PRIMARY_REGION_MEASURED",
            "One whole SEP region matches the independently resolved catalogue primary, outside the companion association region. Coarse photocentre only; independent fresh-frame confirmation remains required.",metrics);
        return new(gate,new(gate,star,primaryPrediction,residual,double.PositiveInfinity),true,spread,[star]);
    }
    private static double Distance(PixelPoint a,PixelPoint b)=>Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
}
