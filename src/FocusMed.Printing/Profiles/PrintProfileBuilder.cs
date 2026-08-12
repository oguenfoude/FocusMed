using FocusMed.Printing.Discovery;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Profiles;

internal sealed class PrintProfileBuilder(ILogger<PrintProfileBuilder> logger) : IPrintProfileBuilder
{
    public IReadOnlyList<PrintProfile> BuildProfiles(PrinterCapabilitySnapshot snapshot)
    {
        var profiles = new List<PrintProfile>();

        var a3Paper = snapshot.PaperSizes.FirstOrDefault(ps => ps.Name.Contains("A3", StringComparison.OrdinalIgnoreCase));
        var a4Paper = snapshot.PaperSizes.FirstOrDefault(ps => ps.Name.Contains("A4", StringComparison.OrdinalIgnoreCase))
            ?? snapshot.PaperSizes.FirstOrDefault();

        // Favorite #1 Profile: Booklet A3
        if (snapshot.SupportsDuplex && a3Paper is not null)
        {
            profiles.Add(new PrintProfile
            {
                Name = "Booklet A3",
                Description = "Livret A3 (imposition 2-up sur feuille A3, pli bord court)",
                IsBooklet = true,
                RequiresDuplex = true,
                UseDuplexShortEdge = true,
                PaperSizeName = a3Paper.Name,
            });
        }

        profiles.Add(new PrintProfile
        {
            Name = "Simple",
            Description = "Recto simple (une seule face)",
            PaperSizeName = a3Paper?.Name ?? a4Paper?.Name,
        });

        if (snapshot.SupportsDuplex)
        {
            profiles.Add(new PrintProfile
            {
                Name = "Duplex Long",
                Description = "Recto-verso (pli bord long)",
                RequiresDuplex = true,
                UseDuplexShortEdge = false,
                PaperSizeName = a3Paper?.Name ?? a4Paper?.Name,
            });

            profiles.Add(new PrintProfile
            {
                Name = "Booklet A4",
                Description = "Livret A4 (imposition 2-up sur feuille A4, pli bord court)",
                IsBooklet = true,
                RequiresDuplex = true,
                UseDuplexShortEdge = true,
                PaperSizeName = a4Paper?.Name ?? "A4",
            });
        }

        if (snapshot.SupportsDuplex)
        {
            profiles.Add(new PrintProfile
            {
                Name = "Booklet",
                Description = "Livret (imposition 2-up, pli bord court)",
                IsBooklet = true,
                RequiresDuplex = true,
                UseDuplexShortEdge = true,
                PaperSizeName = a3Paper?.Name ?? a4Paper?.Name,
            });
        }

        if (snapshot.SupportsColor)
        {
            profiles.Add(new PrintProfile
            {
                Name = "Noir & Blanc",
                Description = "Recto simple, niveaux de gris",
                ForceGrayscale = true,
                PaperSizeName = a3Paper?.Name ?? a4Paper?.Name,
            });

            if (snapshot.SupportsDuplex)
            {
                profiles.Add(new PrintProfile
                {
                    Name = "Noir & Blanc Duplex",
                    Description = "Recto-verso, niveaux de gris",
                    ForceGrayscale = true,
                    RequiresDuplex = true,
                    UseDuplexShortEdge = false,
                    PaperSizeName = a3Paper?.Name ?? a4Paper?.Name,
                });
            }
        }

        var mandatorySizes = new[] { "A4", "A3", "A5", "B5", "B4", "Letter", "Legal", "Tabloid", "Executive", "Statement" };

        var extraPaperSizes = snapshot.PaperSizes
            .Where(ps => ps.Name != (a3Paper?.Name ?? a4Paper?.Name))
            .Where(ps => mandatorySizes.Any(c => ps.Name.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(ps => mandatorySizes.FirstOrDefault(c => ps.Name.Contains(c, StringComparison.OrdinalIgnoreCase)) ?? ps.Name)
            .Select(g => g.First())
            .Take(10);

        foreach (var paperSize in extraPaperSizes)
        {
            profiles.Add(new PrintProfile
            {
                Name = $"Simple - {paperSize.Name}",
                Description = $"Recto simple, papier {paperSize.Name}",
                PaperSizeName = paperSize.Name,
            });

            if (snapshot.SupportsDuplex)
            {
                profiles.Add(new PrintProfile
                {
                    Name = $"Duplex - {paperSize.Name}",
                    Description = $"Recto-verso, papier {paperSize.Name}",
                    RequiresDuplex = true,
                    PaperSizeName = paperSize.Name,
                });

                profiles.Add(new PrintProfile
                {
                    Name = $"Booklet - {paperSize.Name}",
                    Description = $"Livret, papier {paperSize.Name}",
                    IsBooklet = true,
                    RequiresDuplex = true,
                    UseDuplexShortEdge = true,
                    PaperSizeName = paperSize.Name,
                });
            }
        }

        logger.LogDebug("Built {Count} profiles for '{PrinterName}' (Duplex={HasDuplex}, Color={HasColor}, PaperCount={PaperCount})",
            profiles.Count, snapshot.PrinterName, snapshot.SupportsDuplex, snapshot.SupportsColor, snapshot.PaperSizes.Count);

        return profiles;
    }
}
