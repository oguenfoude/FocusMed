using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace FocusMed.Dicom;

public class ScalingEngine
{
    private readonly ILogger<ScalingEngine> _logger;

    public ScalingEngine(ILogger<ScalingEngine> logger)
    {
        _logger = logger;
    }

    public string ProcessImageForPrint(string sourceImagePath, string filmSize, string orientation, int requestedDpi = 300)
    {
        var (targetWidth, targetHeight) = GetFilmDimensions(filmSize);

        if (orientation.Equals("LANDSCAPE", StringComparison.OrdinalIgnoreCase))
        {
            (targetWidth, targetHeight) = (targetHeight, targetWidth);
        }

        using var image = Image.Load<Rgba32>(sourceImagePath);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(targetWidth, targetHeight),
            Mode = ResizeMode.Max
        }));

        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
        image.SaveAsPng(tempFile);

        return tempFile;
    }

    private (int width, int height) GetFilmDimensions(string? filmSize)
    {
        return filmSize?.ToUpperInvariant() switch
        {
            "A3"         => (3508, 4961),
            "8INX10IN"   => (2400, 3000),
            "14INX17IN"  => (4200, 5100),
            "A4" or null => (2480, 3508),
            _            => LogAndReturnA4(filmSize)
        };
    }

    private (int width, int height) LogAndReturnA4(string? filmSize)
    {
        _logger.LogWarning(
            "Unknown filmSize '{FilmSize}'. Falling back to A4 dimensions.",
            filmSize);
        return (2480, 3508);
    }
}
