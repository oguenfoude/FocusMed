using FocusMed.Printing.Discovery;
using FocusMed.Printing.Profiles;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Jobs;

internal sealed class PrintJobValidator(
    IPrinterCapabilityService capabilityService,
    ILogger<PrintJobValidator> logger) : IPrintJobValidator
{
    public async Task<ValidationResult> ValidateAsync(PrintJobRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(request.PdfPath))
        {
            logger.LogWarning("PDF file not found: '{Path}'", request.PdfPath);
            return new ValidationResult { IsValid = false, ErrorMessage = $"Fichier PDF introuvable: {request.PdfPath}" };
        }

        if (request.Copies < 1 || request.Copies > 99)
        {
            return new ValidationResult { IsValid = false, ErrorMessage = "Le nombre de copies doit etre entre 1 et 99." };
        }

        var snapshot = await capabilityService.GetSnapshotAsync(request.PrinterName, ct);

        if (snapshot.PaperSizes.Count == 0 && !string.IsNullOrEmpty(request.Profile.PaperSizeName))
        {
            logger.LogWarning("No paper sizes discovered for '{PrinterName}'", request.PrinterName);
        }

        if (request.Profile.RequiresDuplex && !snapshot.SupportsDuplex)
        {
            logger.LogWarning("Profile '{Profile}' requires duplex but printer '{PrinterName}' does not support it",
                request.Profile.Name, request.PrinterName);
            return new ValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Le mode '{request.Profile.Name}' necessite l'impression recto-verso, mais l'imprimante '{request.PrinterName}' ne le supporte pas."
            };
        }

        return new ValidationResult { IsValid = true };
    }
}
