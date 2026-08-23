using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FocusMed.Printing.Jobs;

internal sealed class RawPrintService(ILogger<RawPrintService> logger) : IRawPrintService
{
    public async Task<bool> PrintPdfAsync(string printerIp, byte[] pdfData, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(printerIp))
        {
            logger.LogWarning("RawPrint: no printer IP configured");
            return false;
        }

        if (pdfData.Length == 0)
        {
            logger.LogWarning("RawPrint: PDF data is empty");
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await client.ConnectAsync(printerIp, port, cts.Token);
            await using var stream = client.GetStream();

            var pjlHeader = BuildPjlHeader(paperSize, duplex, shortEdgeBind);
            await stream.WriteAsync(pjlHeader, cts.Token);

            await stream.WriteAsync(pdfData, cts.Token);
            await stream.FlushAsync(cts.Token);

            logger.LogInformation("RawPrint: sent {Size:N0} bytes to {Ip}:{Port} paper={Paper} duplex={Duplex}", pdfData.Length, printerIp, port, paperSize, duplex);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("RawPrint: timeout sending to {Ip}:{Port} ({Timeout}ms)", printerIp, port, timeoutMs);
            return false;
        }
        catch (SocketException ex)
        {
            logger.LogError(ex, "RawPrint: connection failed to {Ip}:{Port} ({ErrorCode})", printerIp, port, ex.ErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RawPrint: failed to send to {Ip}:{Port}", printerIp, port);
            return false;
        }
    }

    public async Task<bool> PrintPdfAsync(string printerIp, string pdfFilePath, string paperSize = "A4", bool duplex = false, bool shortEdgeBind = false, int port = 9100, int timeoutMs = 30000, CancellationToken ct = default)
    {
        if (!File.Exists(pdfFilePath))
        {
            logger.LogWarning("RawPrint: file not found: {Path}", pdfFilePath);
            return false;
        }

        var pdfData = await File.ReadAllBytesAsync(pdfFilePath, ct);
        return await PrintPdfAsync(printerIp, pdfData, paperSize, duplex, shortEdgeBind, port, timeoutMs, ct);
    }

    private static byte[] BuildPjlHeader(string paperSize, bool duplex, bool shortEdgeBind)
    {
        var sb = new StringBuilder();
        sb.Append("\x1b%-12345X");

        var paper = paperSize.ToUpperInvariant() switch
        {
            "A3" => "A3",
            "A4" => "A4",
            "A5" => "A5",
            "LETTER" => "LETTER",
            "LEGAL" => "LEGAL",
            _ => paperSize.ToUpperInvariant()
        };
        sb.Append($"@PJL SET PAPER={paper}\r\n");
        sb.Append("@PJL SET MEDIATYPE=PLAIN\r\n");

        if (duplex)
        {
            sb.Append("@PJL SET DUPLEX=ON\r\n");
            sb.Append(shortEdgeBind ? "@PJL SET DUPLEXBIND=SHORTEDGE\r\n" : "@PJL SET DUPLEXBIND=LONGEDGE\r\n");
        }

        sb.Append("@PJL ENTER LANGUAGE = PDF\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
