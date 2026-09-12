using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record CatalogPrimaryAstrometry(string CatalogId,string MainId,double Epoch2000RaDegrees,
    double Epoch2000DecDegrees,double ProperMotionRaMasPerYear,double ProperMotionDecMasPerYear,
    double RightAscensionDegrees,double DeclinationDegrees,string Bibliography,string ResponseSha256,
    string SourceUri,DateTimeOffset EpochUtc,string RawResponse);

/// <summary>Optional, exact-ID catalogue witness. Never changes the selected plan,
/// talks to equipment, or infers an identity from a measured image.</summary>
internal static class CatalogPrimaryAstrometryReader
{
    private static readonly HttpClient Client = new() { Timeout=TimeSpan.FromSeconds(8),MaxResponseContentBufferSize=65536 };

    internal static async Task<CatalogPrimaryAstrometry?> ReadAsync(string id,double selectedRa,double selectedDec,CancellationToken token)
    {
        id=id.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id,@"^HIP [0-9]{1,6}$")) return null;
        var query=$"SELECT TOP 2 i.id,b.main_id,b.ra,b.dec,b.pmra,b.pmdec,b.coo_bibcode FROM basic AS b JOIN ident AS i ON i.oidref=b.oid WHERE i.id='{id}'";
        var uri="https://simbad.cds.unistra.fr/simbad/sim-tap/sync?request=doQuery&lang=adql&format=json&query="+Uri.EscapeDataString(query);
        try
        {
            var bytes=await Client.GetByteArrayAsync(uri,token).ConfigureAwait(false);
            return Parse(System.Text.Encoding.UTF8.GetString(bytes),id,selectedRa,selectedDec,DateTimeOffset.UtcNow,uri);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
        catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    internal static CatalogPrimaryAstrometry? Parse(string json,string id,double selectedRa,double selectedDec,DateTimeOffset epoch,string uri)
    {
        using var doc=JsonDocument.Parse(json);
        var root=doc.RootElement;
        if (!root.TryGetProperty("data",out var rows) || rows.ValueKind!=JsonValueKind.Array || rows.GetArrayLength()!=1
            || !root.TryGetProperty("metadata",out var meta) || meta.ValueKind!=JsonValueKind.Array || meta.GetArrayLength()!=7) return null;
        var names=new[]{"id","main_id","ra","dec","pmra","pmdec","coo_bibcode"};
        for(var i=0;i<names.Length;i++)
            if(!meta[i].TryGetProperty("name",out var name) || name.GetString()!=names[i]) return null;
        for(var i=2;i<=3;i++)
            if(!meta[i].TryGetProperty("unit",out var unit) || unit.GetString()!="deg"
                || !meta[i].TryGetProperty("utype",out var type) || !(type.GetString()?.Contains("CS.spaceSys=ICRS CT.epoch=J2000")??false)) return null;
        for(var i=4;i<=5;i++)
            if(!meta[i].TryGetProperty("unit",out var unit) || unit.GetString()!="mas.yr-1") return null;
        var row=rows[0];
        if(row.ValueKind!=JsonValueKind.Array || row.GetArrayLength()!=7 || row[0].GetString()?.Trim()!=id
            || row[1].ValueKind!=JsonValueKind.String || row[6].ValueKind!=JsonValueKind.String) return null;
        var numbers=new double[4];
        for(var i=0;i<4;i++) if(row[i+2].ValueKind!=JsonValueKind.Number || !row[i+2].TryGetDouble(out numbers[i]) || !double.IsFinite(numbers[i])) return null;
        var (ra,dec,pmra,pmdec)=(numbers[0],numbers[1],numbers[2],numbers[3]);
        if(ra is <0 or >=360 || Math.Abs(dec)>85 || Math.Abs(pmra)>10000 || Math.Abs(pmdec)>10000
            || !double.IsFinite(selectedRa) || !double.IsFinite(selectedDec)) return null;
        var years=(epoch-new DateTimeOffset(2000,1,1,12,0,0,TimeSpan.Zero)).TotalDays/365.25;
        if(Math.Abs(years)>100) return null;
        var currentRa=(ra+pmra*years/(3600000*Math.Cos(dec*Math.PI/180))+360)%360;
        var currentDec=dec+pmdec*years/3600000;
        if(G3AcquisitionMotionPlanner.AngularSeparationArcseconds(currentRa,currentDec,selectedRa,selectedDec)>60) return null;
        return new(id,row[1].GetString()!,ra,dec,pmra,pmdec,currentRa,currentDec,row[6].GetString()!,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))),uri,epoch,json);
    }
}
