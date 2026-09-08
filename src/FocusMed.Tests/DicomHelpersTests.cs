using FellowOakDicom;
using FocusMed.Dicom;

namespace FocusMed.Tests;

public class DicomHelpersTests
{
    [Theory]
    [InlineData(null, "Inconnu")]
    [InlineData("", "Inconnu")]
    [InlineData("DOE^JOHN", "DOE JOHN")]
    [InlineData("  ", "Inconnu")]
    [InlineData("MARTIN", "MARTIN")]
    public void FormatPatientName_BehavesLikeDisplaySites(string? input, string expected)
    {
        Assert.Equal(expected, DicomHelpers.FormatPatientName(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Smith", "Smith")]
    [InlineData("a/b\\c", "a_b_c")]
    public void SanitizeFileName_StripsSeparators(string input, string expected)
    {
        Assert.Equal(expected, DicomHelpers.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_NeverReturnsUnknown()
    {
        Assert.Equal("", DicomHelpers.SanitizeFileName(""));
        Assert.DoesNotContain("UNKNOWN", DicomHelpers.SanitizeFileName("x/y"));
    }

    [Theory]
    // child of root -> true
    [InlineData(@"C:\data\archive\study1", @"C:\data\archive", true)]
    [InlineData(@"C:\data\archive\study1\series1", @"C:\data\archive", true)]
    // root itself -> false (must be STRICTLY inside)
    [InlineData(@"C:\data\archive", @"C:\data\archive", false)]
    // above root -> false
    [InlineData(@"C:\data", @"C:\data\archive", false)]
    [InlineData(@"C:\", @"C:\data\archive", false)]
    // sibling -> false
    [InlineData(@"C:\data\other", @"C:\data\archive", false)]
    // different drive -> false
    [InlineData(@"D:\data\archive\study1", @"C:\data\archive", false)]
    public void IsSubdirectoryOf_GuardsRecursiveDeletes(string dir, string root, bool expected)
    {
        Assert.Equal(expected, DicomHelpers.IsSubdirectoryOf(dir, root));
    }

    [Fact]
    public void GetFnv1aHash_IsDeterministic16Hex()
    {
        var a = DicomHelpers.GetFnv1aHash("abc");
        Assert.Equal(a, DicomHelpers.GetFnv1aHash("abc"));
        Assert.NotEqual(a, DicomHelpers.GetFnv1aHash("abd"));
        Assert.Matches("^[0-9A-F]{16}$", a);
    }

    [Fact]
    public void GetDicomDate_ParsesYyyyMMddAsUtc()
    {
        var ds = new DicomDataset { { DicomTag.PatientBirthDate, "19640823" } };
        var date = DicomHelpers.GetDicomDate(ds, DicomTag.PatientBirthDate);
        Assert.NotNull(date);
        Assert.Equal(new DateTime(1964, 8, 23, 0, 0, 0, DateTimeKind.Utc), date);
    }

    // NB: read through PatientName (PN accepts any string). BirthDate (DA)
    // is VR-validated by fo-dicom on write, so invalid values can't even
    // be stored there — the parsing fallback below is what we're testing.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("23/08/1964")]
    public void GetDicomDate_InvalidInputReturnsNull(string value)
    {
        var ds = new DicomDataset { { DicomTag.PatientName, value } };
        Assert.Null(DicomHelpers.GetDicomDate(ds, DicomTag.PatientName));
    }

    [Fact]
    public void GetDicomDate_MissingTagReturnsNull()
    {
        Assert.Null(DicomHelpers.GetDicomDate(new DicomDataset(), DicomTag.PatientBirthDate));
    }
}
