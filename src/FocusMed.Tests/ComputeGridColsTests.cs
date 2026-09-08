using FocusMed.Dashboard.Services;

namespace FocusMed.Tests;

// Regression lock for the aspect-aware grid (PdfService.ComputeGridCols).
// Values verified by hand against the cost function
// |ln(rows/cols) - ln(target)| + 0.35 * waste.
public class ComputeGridColsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveCount_ReturnsOne(int count)
    {
        Assert.Equal(1, PdfService.ComputeGridCols(count));
    }

    [Fact]
    public void SingleImage_ReturnsOne()
    {
        Assert.Equal(1, PdfService.ComputeGridCols(1));
    }

    [Fact]
    public void FourLandscapeImagesOnA4_ReturnsTwoColumns()
    {
        Assert.Equal(2, PdfService.ComputeGridCols(4));
    }

    [Fact]
    public void SixLandscapeImagesOnA4_ReturnsTwoColumns()
    {
        Assert.Equal(2, PdfService.ComputeGridCols(6));
    }

    [Fact]
    public void FourSquareImagesOnA4_ReturnsTwoColumns()
    {
        Assert.Equal(2, PdfService.ComputeGridCols(4, imageAspect: 1.0));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(16)]
    public void Result_IsAlwaysWithinBounds(int count)
    {
        var cols = PdfService.ComputeGridCols(count);
        Assert.InRange(cols, 1, count);
    }
}
