using System.Collections.Concurrent;
using System.IO.Compression;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MiniSoftware;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace FocusMed.Dashboard.Services;

public class PdfService
{
    private readonly string _pdfCacheDir;
    private readonly string _coverTemplatePath;
    private readonly ILogger<PdfService> _logger;
    private readonly FocusMed.Printing.Imposition.IBookletImpositionService _bookletService;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pdfLocks = new();

    public PdfService(ILogger<PdfService> logger, IWebHostEnvironment env, FocusMed.Printing.Imposition.IBookletImpositionService bookletService)
    {
        _logger = logger;
        _bookletService = bookletService;

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        _pdfCacheDir = Path.Combine(dataDir, "pdf-cache");
        Directory.CreateDirectory(_pdfCacheDir);

        _coverTemplatePath = Path.Combine(env.WebRootPath, "cover.docx");
    }

    public string GeneratePrintPdf(
        string patientName,
        string studyDate,
        string studyDescription,
        IReadOnlyList<string> imagePaths,
        string? resumePdfPath = null,
        string pageSize = "A4",
        bool isBooklet = false,
        int imagesPerPage = 1,
        int gapPx = 1,
        int marginPx = 10)
    {
        CleanupOldPdfs();

        var validPaths = imagePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0 && string.IsNullOrEmpty(resumePdfPath)) return "";

        var inputKey = $"{patientName}|{studyDate}|{resumePdfPath}|{imagesPerPage}|{gapPx}|{marginPx}|{string.Join(";", validPaths)}";
        var hashBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(inputKey));
        var hashStr = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var fileName = $"cache_{hashStr}.pdf";
        var finalPath = Path.Combine(_pdfCacheDir, fileName);

        if (File.Exists(finalPath))
        {
            return $"/pdf-cache/{fileName}";
        }

        var pdfLock = _pdfLocks.GetOrAdd(hashStr, _ => new SemaphoreSlim(1, 1));
        pdfLock.Wait();
        try
        {
            if (File.Exists(finalPath))
                return $"/pdf-cache/{fileName}";

            var tempFiles = new List<string>();
            try
            {
                var coverDocxPath = CreateModifiedCoverDocx(patientName, studyDate, tempFiles);
                if (coverDocxPath == null) return "";

                var coverPdfPath = Path.Combine(Path.GetTempPath(), $"cover_{Guid.NewGuid():N}.pdf");
                MiniPdf.ConvertToPdf(coverDocxPath, coverPdfPath);
                tempFiles.Add(coverPdfPath);

                string? resumeFullPath = null;
                if (!string.IsNullOrEmpty(resumePdfPath))
                {
                    var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
                    resumeFullPath = Path.Combine(dataDir, resumePdfPath);
                    if (!File.Exists(resumeFullPath))
                    {
                        _logger.LogWarning("Resume PDF not found at {Path}", resumeFullPath);
                        resumeFullPath = null;
                    }
                }

                string? imagesPdfPath = null;
                if (validPaths.Count > 0)
                {
                    imagesPdfPath = Path.Combine(Path.GetTempPath(), $"images_{Guid.NewGuid():N}.pdf");
                    GenerateImagesPdf(validPaths, imagesPdfPath, imagesPerPage, gapPx, marginPx);
                    tempFiles.Add(imagesPdfPath);
                }

                var tempMergedA4 = Path.Combine(Path.GetTempPath(), $"merged_a4_{Guid.NewGuid():N}.pdf");
                tempFiles.Add(tempMergedA4);
                MergePdfs(tempMergedA4, coverPdfPath, resumeFullPath, imagesPdfPath);

                File.Copy(tempMergedA4, finalPath, overwrite: true);

                return $"/pdf-cache/{fileName}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate PDF");
                return "";
            }
            finally
            {
                foreach (var f in tempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
            }
        }
        finally
        {
            pdfLock.Release();
        }
    }

    private string? CreateModifiedCoverDocx(string patientName, string studyDate, List<string> tempFiles)
    {
        if (!File.Exists(_coverTemplatePath))
        {
            _logger.LogWarning("Cover template not found at {Path}", _coverTemplatePath);
            return null;
        }

        var tempDocx = Path.Combine(Path.GetTempPath(), $"cover_mod_{Guid.NewGuid():N}.docx");
        tempFiles.Add(tempDocx);

        try
        {
            File.Copy(_coverTemplatePath, tempDocx, true);

            // Modify the docx (which is a zip) by replacing placeholders in document.xml
            using (var archive = ZipFile.Open(tempDocx, ZipArchiveMode.Update))
            {
                var docEntry = archive.GetEntry("word/document.xml");
                if (docEntry != null)
                {
                    using var stream = docEntry.Open();
                    using var reader = new StreamReader(stream);
                    var xml = reader.ReadToEnd();

                    xml = xml.Replace("{{PatientName}}", patientName);
                    xml = xml.Replace("{{StudyDate}}", studyDate);

                    // Rewrite the entry
                    stream.Position = 0;
                    stream.SetLength(0);
                    using var writer = new StreamWriter(stream);
                    writer.Write(xml);
                }
            }

            return tempDocx;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify cover template");
            try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
            tempFiles.Remove(tempDocx);
            return null;
        }
    }

    private void GenerateImagesPdf(IReadOnlyList<string> imagePaths, string outputPath, int imagesPerPage, int gapPx, int marginPx)
    {
        var questPageSize = PageSizes.A4.Portrait();
        var perPage = Math.Max(1, imagesPerPage);
        var gap = (float)Math.Max(0, gapPx);
        var margin = (float)Math.Max(0, marginPx);

        int cols = perPage switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 2,
            5 or 6 => 3,
            7 or 8 or 9 => 3,
            10 or 11 or 12 => 4,
            13 or 14 or 15 or 16 => 4,
            _ => (int)Math.Ceiling(Math.Sqrt(perPage))
        };

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            for (int i = 0; i < imagePaths.Count; i += perPage)
            {
                var batch = imagePaths.Skip(i).Take(perPage).ToList();
                container.Page(page =>
                {
                    page.Size(questPageSize);
                    page.MarginHorizontal(margin);
                    page.MarginVertical(margin);

                    page.Content().Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            for (int c = 0; c < cols; c++)
                                columns.RelativeColumn();
                        });

                        foreach (var imgPath in batch)
                        {
                            try
                            {
                                var imgBytes = File.ReadAllBytes(imgPath);
                                table.Cell()
                                    .Padding(gap / 2f)
                                    .AlignCenter()
                                    .AlignMiddle()
                                    .Image(imgBytes)
                                    .FitArea();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to add image to PDF: {Path}", imgPath);
                            }
                        }
                    });
                });
            }
        });

        using var fs = File.Create(outputPath);
        document.GeneratePdf(fs);
    }




    private void MergePdfs(string outputPath, string coverPdfPath, string? resumePdfPath, string? imagesPdfPath)
    {
        using var outputDocument = new PdfDocument();

        // Cover PDF (always present)
        using (var doc = PdfReader.Open(coverPdfPath, PdfDocumentOpenMode.Import))
        {
            foreach (var page in doc.Pages)
                outputDocument.AddPage(page);
        }

        // Resume PDF (optional — user's Word document)
        if (!string.IsNullOrEmpty(resumePdfPath) && File.Exists(resumePdfPath))
        {
            try
            {
                using var doc = PdfReader.Open(resumePdfPath, PdfDocumentOpenMode.Import);
                foreach (var page in doc.Pages)
                    outputDocument.AddPage(page);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid resume PDF skipped: {Path}", resumePdfPath);
            }
        }

        // Images PDF (optional — may be empty if only resume requested)
        if (!string.IsNullOrEmpty(imagesPdfPath) && File.Exists(imagesPdfPath))
        {
            using (var doc = PdfReader.Open(imagesPdfPath, PdfDocumentOpenMode.Import))
            {
                foreach (var page in doc.Pages)
                    outputDocument.AddPage(page);
            }
        }

        // Pad with blank pages so total page count is a multiple of 4 (e.g. 4 pages for 1 A3 sheet),
        // ensuring Page 4 (the outer back cover) remains completely EMPTY/BLANK.
        int currentPages = outputDocument.Pages.Count;
        int remainder = currentPages % 4;
        if (remainder != 0)
        {
            int needed = 4 - remainder;
            for (int i = 0; i < needed; i++)
            {
                var blankPage = outputDocument.AddPage();
                blankPage.Width = PdfSharpCore.Drawing.XUnit.FromMillimeter(210);
                blankPage.Height = PdfSharpCore.Drawing.XUnit.FromMillimeter(297);
            }
        }

        outputDocument.Save(outputPath);
    }

    public void DeletePdf(string pdfUrl)
    {
        if (string.IsNullOrEmpty(pdfUrl)) return;
        var relativePath = pdfUrl.TrimStart('/');
        var filePath = Path.Combine(_pdfCacheDir, Path.GetFileName(relativePath));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete PDF {Path}", pdfUrl); return; }
        }
    }

    private void CleanupOldPdfs(int maxAgeMinutes = 60)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-maxAgeMinutes);
        foreach (var file in Directory.GetFiles(_pdfCacheDir, "*.pdf"))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    File.Delete(file);
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Cleanup skipped (locked): {File}", file);
                    break;
                }
            }
        }
    }
}
