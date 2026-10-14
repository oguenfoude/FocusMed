using FocusMed.Dashboard.Services;

namespace FocusMed.Tests;

/// <summary>
/// Default preview rules shared by StudyDetails and PdfPrewarmService must agree:
/// selection (saved-first else PRINT-default), per-page math, print-mode mapping.
/// </summary>
public sealed class PdfPreviewDefaultsTests
{
    [Fact]
    public void ResolveSelection_SavedWinsOverDefault()
    {
        var available = new List<(string Sop, string Source)> { ("A", "PRINT"), ("B", "CT"), ("C", "CT") };
        var saved = new Dictionary<string, bool?> { ["B"] = true };
        var selected = PdfPreviewDefaults.ResolveSelection(available, saved);
        Assert.Equal(new HashSet<string> { "B" }, selected);
    }

    [Fact]
    public void ResolveSelection_NoSaved_MixedSelectsPrintOnly()
    {
        var available = new List<(string Sop, string Source)> { ("A", "PRINT"), ("B", "CT") };
        var selected = PdfPreviewDefaults.ResolveSelection(available, new Dictionary<string, bool?>());
        Assert.Equal(new HashSet<string> { "A" }, selected);
    }

    [Fact]
    public void ResolveSelection_NoSaved_PureCStoreSelectsAll()
    {
        var available = new List<(string Sop, string Source)> { ("A", "CT"), ("B", "CT") };
        var selected = PdfPreviewDefaults.ResolveSelection(available, new Dictionary<string, bool?>());
        Assert.Equal(new HashSet<string> { "A", "B" }, selected);
    }

    [Fact]
    public void ResolveSelection_AllDeselected_StaysEmpty()
    {
        var available = new List<(string Sop, string Source)> { ("A", "PRINT") };
        var saved = new Dictionary<string, bool?> { ["A"] = false };
        Assert.Empty(PdfPreviewDefaults.ResolveSelection(available, saved));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(7, 4)]
    [InlineData(13, 5)]
    public void ComputeAutoPerPage_MatchesBadgeMath(int count, int expected)
    {
        Assert.Equal(expected, PdfPreviewDefaults.ComputeAutoPerPage(count));
    }

    [Theory]
    [InlineData("BookletA3", "A4", true)]
    [InlineData("FlatA3", "A3", false)]
    [InlineData("FlatA4", "A4", false)]
    public void ForMode_MapsPrintModes(string mode, string size, bool booklet)
    {
        Assert.Equal((size, booklet), PdfPreviewDefaults.ForMode(mode));
    }
}
