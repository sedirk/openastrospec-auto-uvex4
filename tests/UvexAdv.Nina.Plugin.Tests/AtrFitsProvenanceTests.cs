using System.Text;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrFitsProvenanceTests
{
    [Fact]
    public void VerifiesStableTargetAndIndependentRunFields()
    {
        var path = WriteFits(
            ("SIMPLE", "T", false),
            ("OBJECT", "3C 273", true),
            ("OBSRUNID", "run-20260825", true),
            ("UVEXSTG", "SCIENCE", true),
            ("UVEXCID", "capture-1", true),
            ("NIGHTSET", "night-a", true),
            ("IMAGETYP", "LIGHT", true),
            ("CATALOG", "PGC 41121", true));
        try
        {
            var result = AtrFitsProvenance.Verify(
                path,
                new FitsProvenanceExpectation(
                    "3C 273",
                    "run-20260825",
                    "SCIENCE",
                    "capture-1",
                    "night-a",
                    "LIGHT",
                    "PGC 41121"));

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
            Assert.Equal("3C 273", result.Headers["OBJECT"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportsMismatchWithoutChangingTheSavedFits()
    {
        var path = WriteFits(
            ("SIMPLE", "T", false),
            ("OBJECT", "run-science-1", true),
            ("OBSRUNID", "run-a", true),
            ("UVEXSTG", "PROBE", true),
            ("UVEXCID", "capture-a", true),
            ("NIGHTSET", "night-a", true),
            ("IMAGETYP", "SNAPSHOT", true));
        var before = File.ReadAllBytes(path);
        try
        {
            var result = AtrFitsProvenance.Verify(
                path,
                new FitsProvenanceExpectation(
                    "Algol",
                    "run-a",
                    "PROBE",
                    "capture-a",
                    "night-a",
                    "SNAPSHOT",
                    string.Empty));

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains("OBJECT", StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("天津四 Deneb", "SNAPSHOT", "PROBE")]
    [InlineData("天津四", "LIGHT", "SCIENCE")]
    [InlineData("β Orionis", "LIGHT", "SCIENCE")]
    public void NativeNinaWriterPreservesVersionedUnicodeLongIdentityAndActualImageType(
        string target, string imageType, string role)
    {
        var expected = new FitsProvenanceExpectation(target, "run-20260907", role, "capture-2",
            "DF-UVEX4-NIGHT-SETUP-20260826T175011718Z-M2-HOLD-12500-HORIZON30-WEAK-SUPERVISION",
            imageType, "HIP 102098", HeaderSchemaVersion: 2);
        var path = WriteNativeFits(expected);
        try
        {
            var result = AtrFitsProvenance.Verify(path, expected);
            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
            Assert.Equal("LIGHT", result.Headers["IMAGETYP"]);
            Assert.Equal(imageType, result.Headers["NINATYP"]);
            Assert.DoesNotContain('?', result.Headers["OBJECT"]);
            Assert.NotEqual(expected.NightSetupId, result.Headers["NIGHTSET"]);
            var encoded = string.Concat(result.Headers.Where(h => h.Key.StartsWith("OBJ", StringComparison.Ordinal) &&
                h.Key.Length == 7 && char.IsDigit(h.Key[3])).OrderBy(h => h.Key).Select(h => h.Value));
            Assert.Equal(target, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("OBJ0001")]
    [InlineData("NST0002")]
    [InlineData("UVEXSTG")]
    [InlineData("NINATYP")]
    public void VersionedIdentityStillRejectsMissingOrChangedLosslessFields(string changedKey)
    {
        var expected = new FitsProvenanceExpectation("天津四 Deneb", "run-a", "PROBE", "capture-a",
            new string('N', 90), "SNAPSHOT", "HIP 102098", HeaderSchemaVersion: 2);
        var path = WriteNativeFits(expected, changedKey);
        var before = File.ReadAllBytes(path);
        try
        {
            var result = AtrFitsProvenance.Verify(path, expected);
            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Contains(changedKey, StringComparison.Ordinal));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    private static string WriteNativeFits(FitsProvenanceExpectation expected, string? changedKey = null)
    {
        var metadata = new ImageMetaData();
        metadata.Target.Name = AtrFitsProvenance.FitsTargetName(expected);
        metadata.Image.ImageType = expected.ImageType;
        foreach (var field in AtrFitsProvenance.CreateIdentityHeaders(expected))
        {
            metadata.GenericHeaders.Add(new StringMetaDataHeader(field.Key,
                field.Key == changedKey ? "changed" : field.Value, "OpenAstroSpec identity / UTF8-B64 provenance"));
        }
        var header = new FITSHeader(2, 2);
        header.Add("SIMPLE", true, "test header only");
        header.PopulateFromMetaData(metadata);
        var path = Path.Combine(Path.GetTempPath(), $"openastrospec-nina-fits-{Guid.NewGuid():N}.fits");
        using var stream = File.Create(path);
        header.Write(stream);
        var padded = ((stream.Length + 2879) / 2880) * 2880;
        while (stream.Length < padded) stream.WriteByte(32);
        return path;
    }

    private static string WriteFits(params (string Key, string Value, bool Quoted)[] values)
    {
        var cards = new List<string>();
        foreach (var (key, value, quoted) in values)
        {
            var formatted = quoted ? $"'{value.Replace("'", "''", StringComparison.Ordinal)}'" : value;
            cards.Add(($"{key,-8}= {formatted}").PadRight(80));
        }
        cards.Add("END".PadRight(80));
        var header = string.Concat(cards);
        header = header.PadRight(((header.Length + 2879) / 2880) * 2880);
        var path = Path.Combine(Path.GetTempPath(), $"openastrospec-fits-{Guid.NewGuid():N}.fits");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(header));
        return path;
    }
}
