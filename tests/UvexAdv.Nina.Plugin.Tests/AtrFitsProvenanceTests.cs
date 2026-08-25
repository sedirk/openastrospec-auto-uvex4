using System.Text;
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
