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

    public PdfService(ILogger<PdfService> logger, IWebHostEnvironment env)
    {
        _logger = logger;

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
        string? resumePdfPath = null)
    {
        CleanupOldPdfs();

        var validPaths = imagePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0 && string.IsNullOrEmpty(resumePdfPath)) return "";

        var fileName = $"{Guid.NewGuid():N}.pdf";
        var finalPath = Path.Combine(_pdfCacheDir, fileName);

        var tempFiles = new List<string>();
        try
        {
            // Step 1: Create modified cover.docx with replaced placeholders
            var coverDocxPath = CreateModifiedCoverDocx(patientName, studyDate, tempFiles);
            if (coverDocxPath == null) return "";

            // Step 2: Convert cover.docx → cover.pdf using MiniPdf
            var coverPdfPath = Path.Combine(Path.GetTempPath(), $"cover_{Guid.NewGuid():N}.pdf");
            MiniPdf.ConvertToPdf(coverDocxPath, coverPdfPath);
            tempFiles.Add(coverPdfPath);

            // Step 3: Prepare resume PDF path (copy from resumes folder if exists)
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

            // Step 4: Generate image pages with QuestPDF → images.pdf
            string? imagesPdfPath = null;
            if (validPaths.Count > 0)
            {
                imagesPdfPath = Path.Combine(Path.GetTempPath(), $"images_{Guid.NewGuid():N}.pdf");
                GenerateImagesPdf(validPaths, imagesPdfPath);
                tempFiles.Add(imagesPdfPath);
            }

            // Step 5: Merge all PDFs using PdfSharpCore
            MergePdfs(finalPath, coverPdfPath, resumeFullPath, imagesPdfPath);

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

    private void GenerateImagesPdf(IReadOnlyList<string> imagePaths, string outputPath)
    {
        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            foreach (var imgPath in imagePaths)
            {
                try
                {
                    var imgBytes = File.ReadAllBytes(imgPath);
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4.Portrait());
                        page.MarginHorizontal(15f);
                        page.MarginVertical(15f);
                        page.Content()
                            .AlignCenter()
                            .AlignMiddle()
                            .Image(imgBytes)
                            .FitArea();
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to add image to PDF: {Path}", imgPath);
                }
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

        // Normalize ALL pages to A4 portrait (595.28 x 841.89 pt = 210mm x 297mm)
        const double a4WidthPt = 595.28;
        const double a4HeightPt = 841.89;
        foreach (PdfPage page in outputDocument.Pages)
        {
            page.Width = a4WidthPt;
            page.Height = a4HeightPt;
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
