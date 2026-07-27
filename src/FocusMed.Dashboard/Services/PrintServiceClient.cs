using System.Net.Http.Json;

namespace FocusMed.Dashboard.Services;

public interface IPrintServiceClient
{
    Task<PrintResult> PrintAsync(PrintRequest request, CancellationToken ct = default);
    Task<JobStatus> GetJobStatusAsync(string printerName, int jobId, CancellationToken ct = default);
    Task<IReadOnlyList<PrinterInfo>> GetConfiguredPrintersAsync(CancellationToken ct = default);
    Task<PrinterCapabilities?> GetCapabilitiesAsync(string printerName, CancellationToken ct = default);
}

public sealed record PrintRequest(
    string PdfPath,
    string PrinterName,
    int Copies = 1,
    bool Duplex = false,
    bool BookletMode = false);

public sealed record PrintResult(bool Success, int? JobId, string? ErrorMessage);

public sealed record JobStatus(string State, string? ErrorMessage);

public sealed record PrinterInfo(string Name, bool Enabled, string Protocol, bool CanDuplex, int PaperSizeCount);

public sealed record PaperSizeInfo(string Name, int WidthHundredthsMm, int HeightHundredthsMm, string Kind);

public sealed record PrinterCapabilities(
    string Name,
    bool IsAvailable,
    bool CanDuplex,
    IReadOnlyList<string> SupportedDuplexModes,
    IReadOnlyList<PaperSizeInfo> SupportedPaperSizes);

public sealed class PrintServiceClient : IPrintServiceClient
{
    private readonly HttpClient _client;
    private readonly ILogger<PrintServiceClient> _logger;

    public PrintServiceClient(HttpClient client, ILogger<PrintServiceClient> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<PrintResult> PrintAsync(PrintRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.PostAsJsonAsync("/print", request, ct);
            var result = await response.Content.ReadFromJsonAsync<PrintResult>(cancellationToken: ct);
            if (result == null)
                return new PrintResult(false, null, "Reponse vide du service d'impression.");
            return result;
        }
        catch (TaskCanceledException)
        {
            return new PrintResult(false, null, "Delai d'attente depasse pour joindre le service d'impression.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "PrintService unreachable");
            return new PrintResult(false, null, $"Impossible de joindre le service d'impression ({_client.BaseAddress}) : {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling PrintService");
            return new PrintResult(false, null, $"Erreur inattendue : {ex.Message}");
        }
    }

    public async Task<JobStatus> GetJobStatusAsync(string printerName, int jobId, CancellationToken ct = default)
    {
        try
        {
            var s = await _client.GetFromJsonAsync<JobStatus>($"/job-status/{Uri.EscapeDataString(printerName)}/{jobId}", ct);
            return s ?? new JobStatus("Error", "Reponse vide du service d'impression.");
        }
        catch (Exception ex)
        {
            return new JobStatus("Error", ex.Message);
        }
    }

    public async Task<IReadOnlyList<PrinterInfo>> GetConfiguredPrintersAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await _client.GetFromJsonAsync<List<PrinterInfo>>("/printers", ct);
            return (IReadOnlyList<PrinterInfo>?)list ?? Array.Empty<PrinterInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PrintService unreachable when listing printers");
            return Array.Empty<PrinterInfo>();
        }
    }

    public async Task<PrinterCapabilities?> GetCapabilitiesAsync(string printerName, CancellationToken ct = default)
    {
        try
        {
            return await _client.GetFromJsonAsync<PrinterCapabilities>(
                $"/printers/{Uri.EscapeDataString(printerName)}/capabilities", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get capabilities for {Printer}", printerName);
            return null;
        }
    }
}
