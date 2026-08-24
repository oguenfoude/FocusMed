using System.Collections.Concurrent;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace FocusMed.Dashboard.Services;

public class PdfService
{
    private readonly string _pdfCacheDir;
    private readonly string _coverLogoPath;
    private readonly string _coverDocxPath;
    private readonly ILogger<PdfService> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _pdfLocks = new();
    private static readonly ConcurrentDictionary<string, int> _pdfLockCounts = new();

    public PdfService(ILogger<PdfService> logger, IWebHostEnvironment env)
    {
        _logger = logger;

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        _pdfCacheDir = Path.Combine(dataDir, "pdf-cache");
        Directory.CreateDirectory(_pdfCacheDir);

        _coverDocxPath = Path.Combine(dataDir, "cover.docx");
        _coverLogoPath = Path.Combine(dataDir, "cover-logo.jpg");
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
        CleanupOldPdfsAsync().GetAwaiter().GetResult();

        var validPaths = imagePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0 && string.IsNullOrEmpty(resumePdfPath)) return "";

        var inputKey = $"{patientName}|{studyDate}|{resumePdfPath}|{imagesPerPage}|{gapPx}|{marginPx}|{pageSize}|{isBooklet}|{string.Join(";", validPaths)}";
        var hashBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(inputKey));
        var hashStr = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var fileName = $"cache_{hashStr}.pdf";
        var finalPath = Path.Combine(_pdfCacheDir, fileName);

        if (File.Exists(finalPath))
        {
            return $"/pdf-cache/{fileName}";
        }

        var pdfLock = AcquirePdfLockRef(hashStr);
        pdfLock.Wait();
        try
        {
            if (File.Exists(finalPath))
                return $"/pdf-cache/{fileName}";

            var tempFiles = new List<string>();
            try
            {
                var coverPdfPath = Path.Combine(Path.GetTempPath(), $"cover_{Guid.NewGuid():N}.pdf");
                GenerateCoverPdf(patientName, studyDate, coverPdfPath, pageSize);
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
                    GenerateImagesPdf(validPaths, imagesPdfPath, imagesPerPage, gapPx, marginPx, pageSize);
                    tempFiles.Add(imagesPdfPath);
                }

                var tempMerged = Path.Combine(Path.GetTempPath(), $"merged_{Guid.NewGuid():N}.pdf");
                tempFiles.Add(tempMerged);
                MergePdfs(tempMerged, coverPdfPath, resumeFullPath, imagesPdfPath, pageSize);

                File.Copy(tempMerged, finalPath, overwrite: true);

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
            ReleasePdfLockRef(hashStr, pdfLock);
        }
    }

    // Gate makes acquire (GetOrAdd + increment) atomic with release-check (decrement + remove).
    // Without refcounting, a thread blocked on Wait while the current holder removes the entry
    // would race a newcomer's freshly created semaphore — both inside the critical section.
    private static readonly object _pdfLockGate = new();

    private static SemaphoreSlim AcquirePdfLockRef(string hash)
    {
        lock (_pdfLockGate)
        {
            var semaphore = _pdfLocks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
            _pdfLockCounts.AddOrUpdate(hash, 1, (_, c) => c + 1);
            return semaphore;
        }
    }

    private static void ReleasePdfLockRef(string hash, SemaphoreSlim semaphore)
    {
        lock (_pdfLockGate)
        {
            var remaining = _pdfLockCounts.AddOrUpdate(hash, 0, (_, c) => c - 1);
            semaphore.Release();
            if (remaining <= 0)
            {
                _pdfLockCounts.TryRemove(hash, out _);
                _pdfLocks.TryRemove(hash, out _);
            }
        }
    }

    public string GenerateBookletPrintPdf(
        string patientName,
        string studyDate,
        string studyDescription,
        IReadOnlyList<string> imagePaths,
        string? resumePdfPath = null,
        int imagesPerPage = 1,
        int gapPx = 1,
        int marginPx = 10)
    {
        var validPaths = imagePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0 && string.IsNullOrEmpty(resumePdfPath)) return "";

        var tempFiles = new List<string>();
        try
        {
            var coverPdfPath = Path.Combine(Path.GetTempPath(), $"bcover_{Guid.NewGuid():N}.pdf");
            GenerateCoverPdf(patientName, studyDate, coverPdfPath, "A4");
            tempFiles.Add(coverPdfPath);

            string? resumeFullPath = null;
            if (!string.IsNullOrEmpty(resumePdfPath))
            {
                var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
                resumeFullPath = Path.Combine(dataDir, resumePdfPath);
                if (!File.Exists(resumeFullPath)) resumeFullPath = null;
            }

            string? imagesPdfPath = null;
            if (validPaths.Count > 0)
            {
                imagesPdfPath = Path.Combine(Path.GetTempPath(), $"bimages_{Guid.NewGuid():N}.pdf");
                GenerateImagesPdf(validPaths, imagesPdfPath, imagesPerPage, gapPx, marginPx, "A4");
                tempFiles.Add(imagesPdfPath);
            }

            var tempMerged = Path.Combine(Path.GetTempPath(), $"bmerged_{Guid.NewGuid():N}.pdf");
            tempFiles.Add(tempMerged);
            MergePdfs(tempMerged, coverPdfPath, resumeFullPath, imagesPdfPath, "A4");

            var dataDirOut = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var bookletDir = Path.Combine(dataDirOut, "pdf-cache");
            var fileName = $"booklet_{Guid.NewGuid():N}.pdf";
            var finalPath = Path.Combine(bookletDir, fileName);
            File.Copy(tempMerged, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    public string GenerateFlatPrintPdf(
        string patientName,
        string studyDate,
        string studyDescription,
        IReadOnlyList<string> imagePaths,
        string? resumePdfPath = null,
        int imagesPerPage = 1,
        int gapPx = 1,
        int marginPx = 10)
    {
        var validPaths = imagePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0 && string.IsNullOrEmpty(resumePdfPath)) return "";

        var tempFiles = new List<string>();
        try
        {
            var coverPdfPath = Path.Combine(Path.GetTempPath(), $"fcover_{Guid.NewGuid():N}.pdf");
            GenerateCoverPdf(patientName, studyDate, coverPdfPath, "A3");
            tempFiles.Add(coverPdfPath);

            string? resumeFullPath = null;
            if (!string.IsNullOrEmpty(resumePdfPath))
            {
                var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
                resumeFullPath = Path.Combine(dataDir, resumePdfPath);
                if (!File.Exists(resumeFullPath)) resumeFullPath = null;
            }

            string? imagesPdfPath = null;
            if (validPaths.Count > 0)
            {
                imagesPdfPath = Path.Combine(Path.GetTempPath(), $"fimages_{Guid.NewGuid():N}.pdf");
                GenerateImagesPdf(validPaths, imagesPdfPath, imagesPerPage, gapPx, marginPx, "A3");
                tempFiles.Add(imagesPdfPath);
            }

            var tempMerged = Path.Combine(Path.GetTempPath(), $"fmerged_{Guid.NewGuid():N}.pdf");
            tempFiles.Add(tempMerged);
            MergePdfs(tempMerged, coverPdfPath, resumeFullPath, imagesPdfPath, "A3");

            var dataDirOut = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
            var flatDir = Path.Combine(dataDirOut, "pdf-cache");
            var fileName = $"flat_{Guid.NewGuid():N}.pdf";
            var finalPath = Path.Combine(flatDir, fileName);
            File.Copy(tempMerged, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            foreach (var f in tempFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }
    }

    private void GenerateCoverPdf(string patientName, string studyDate, string outputPath, string pageSize = "A4")
    {
        var patientDisplay = patientName.Replace("^", " ");

        if (!File.Exists(_coverDocxPath))
        {
            _logger.LogWarning("cover.docx not found at {Path}, using QuestPDF fallback", _coverDocxPath);
            GenerateCoverPdfFallback(patientDisplay, studyDate, outputPath, pageSize);
            return;
        }

        if (pageSize.Equals("A3", StringComparison.OrdinalIgnoreCase))
        {
            GenerateCoverPdfFallback(patientDisplay, studyDate, outputPath, pageSize);
            return;
        }

        try
        {
            var tempDocx = Path.Combine(Path.GetTempPath(), $"cover_{Guid.NewGuid():N}.docx");
            try
            {
                File.Copy(_coverDocxPath, tempDocx, true);
                ConvertDocxToPdfViaWord(tempDocx, outputPath, patientDisplay, studyDate);

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length < 1000)
                {
                    _logger.LogWarning("Word COM produced empty PDF, using QuestPDF fallback");
                    GenerateCoverPdfFallback(patientDisplay, studyDate, outputPath, pageSize);
                }
            }
            finally
            {
                try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Word COM failed, using QuestPDF fallback");
            GenerateCoverPdfFallback(patientDisplay, studyDate, outputPath, pageSize);
        }
    }

    private void ConvertDocxToPdfViaWord(string docxPath, string outputPath, string patientName, string studyDate)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application")
            ?? throw new InvalidOperationException("Word.Application COM not available");

        dynamic? word = null;
        try
        {
            word = Activator.CreateInstance(wordType);
            if (word is null) throw new InvalidOperationException("Failed to create Word instance");
            word.Visible = false;
            word.DisplayAlerts = 0;

            var doc = word.Documents.Open(docxPath, ReadOnly: true, AddToRecentFiles: false);
            try
            {
                var find = doc.Content.Find;
                find.ClearFormatting();
                find.Text = "{{PatientName}}";
                find.Replacement.Text = patientName;
                find.Execute(Replace: 2);

                find.ClearFormatting();
                find.Text = "{{StudyDate}}";
                find.Replacement.Text = studyDate;
                find.Execute(Replace: 2);

                doc.SaveAs2(outputPath, FileFormat: 17);
            }
            finally
            {
                doc.Close(SaveChanges: 0);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(doc);
            }
        }
        finally
        {
            if (word is not null)
            {
                word.Quit();
                System.Runtime.InteropServices.Marshal.ReleaseComObject(word);
            }
        }
    }

    private void GenerateCoverPdfFallback(string patientName, string studyDate, string outputPath, string pageSize = "A4")
    {
        var logoBytes = File.Exists(_coverLogoPath) ? File.ReadAllBytes(_coverLogoPath) : null;
        var isA3 = pageSize.Equals("A3", StringComparison.OrdinalIgnoreCase);
        var pageWidth = isA3 ? PageSizes.A3.Width : PageSizes.A4.Width;
        var pageHeight = isA3 ? PageSizes.A3.Height : PageSizes.A4.Height;

        QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(pageWidth, pageHeight);
                page.Margin(0);

                page.Content().Layers(layers =>
                {
                    if (logoBytes is not null)
                    {
                        layers.Layer().Image(logoBytes).FitUnproportionally();
                    }

                    layers.PrimaryLayer().AlignMiddle().Column(col =>
                    {
                        col.Item().PaddingBottom(20).Row(row =>
                        {
                            row.RelativeItem().AlignRight().PaddingRight(10)
                                .Text("Patient :").FontSize(16).Bold().FontColor("#333333");
                            row.RelativeItem().PaddingLeft(10)
                                .Text(patientName).FontSize(22).Bold().FontColor("#1a5276");
                        });

                        col.Item().Row(row =>
                        {
                            row.RelativeItem().AlignRight().PaddingRight(10)
                                .Text("Examen :").FontSize(16).Bold().FontColor("#333333");
                            row.RelativeItem().PaddingLeft(10)
                                .Text(studyDate).FontSize(22).Bold().FontColor("#1a5276");
                        });
                    });
                });
            });
        }).GeneratePdf(outputPath);
    }

    private void GenerateImagesPdf(IReadOnlyList<string> imagePaths, string outputPath, int imagesPerPage, int gapPx, int marginPx, string pageSize = "A4")
    {
        var isA3 = pageSize.Equals("A3", StringComparison.OrdinalIgnoreCase);
        var questPageSize = isA3 ? PageSizes.A3.Portrait() : PageSizes.A4.Portrait();
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

    private void MergePdfs(string outputPath, string coverPdfPath, string? resumePdfPath, string? imagesPdfPath, string pageSize = "A4")
    {
        using var outputDocument = new PdfDocument();

        using (var doc = PdfReader.Open(coverPdfPath, PdfDocumentOpenMode.Import))
        {
            foreach (var page in doc.Pages)
                outputDocument.AddPage(page);
        }

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

        if (!string.IsNullOrEmpty(imagesPdfPath) && File.Exists(imagesPdfPath))
        {
            using (var doc = PdfReader.Open(imagesPdfPath, PdfDocumentOpenMode.Import))
            {
                foreach (var page in doc.Pages)
                    outputDocument.AddPage(page);
            }
        }

        int currentPages = outputDocument.Pages.Count;
        int remainder = currentPages % 4;
        if (remainder != 0)
        {
            int needed = 4 - remainder;
            double padW = pageSize.Equals("A3", StringComparison.OrdinalIgnoreCase) ? 297 : 210;
            double padH = pageSize.Equals("A3", StringComparison.OrdinalIgnoreCase) ? 420 : 297;
            for (int i = 0; i < needed; i++)
            {
                var blankPage = outputDocument.AddPage();
                blankPage.Width = PdfSharpCore.Drawing.XUnit.FromMillimeter(padW);
                blankPage.Height = PdfSharpCore.Drawing.XUnit.FromMillimeter(padH);
            }
        }

        outputDocument.Save(outputPath);
    }

    public async Task DeletePdfAsync(string pdfUrl)
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
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete PDF {Path}", pdfUrl); return; }
        }
    }

    public void DeletePdf(string pdfUrl)
    {
        DeletePdfAsync(pdfUrl).GetAwaiter().GetResult();
    }

    private async Task CleanupOldPdfsAsync(int maxAgeMinutes = 60)
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
                    await Task.Delay(200);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(200);
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
