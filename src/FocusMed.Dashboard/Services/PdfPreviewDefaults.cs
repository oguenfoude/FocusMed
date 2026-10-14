using FocusMed.Data.Entities;
using FocusMed.Dicom;

namespace FocusMed.Dashboard.Services;

/// <summary>
/// Single source of truth for the default print-preview request: which frames are
/// selected, how many images go per page, and which page size/booklet mode.
/// Used by StudyDetails (open + live regen) AND PdfPrewarmService (background warm-up),
/// so a warmed preview is byte-identical to what the page would generate on open.
/// </summary>
public static class PdfPreviewDefaults
{
    public const string DefaultPrintMode = "BookletA3";
    public const int DefaultGapPx = 1;
    public const int DefaultMarginPx = 10;

    public sealed record PreviewRequest(
        string PatientName,
        string StudyDate,
        string Description,
        List<string> ImagePaths,
        string? ResumePdfPath,
        string PageSize,
        bool IsBooklet,
        int ImagesPerPage,
        int GapPx,
        int MarginPx);

    /// <summary>Builds the default preview request, or null when there is nothing to render.</summary>
    public static PreviewRequest? BuildDefault(Study study, IReadOnlyList<FrameResult> frames)
    {
        var available = frames.Where(f => f.FileExists && !string.IsNullOrEmpty(f.PngPath)).ToList();
        if (available.Count == 0) return null;

        var saved = study.Series
            .SelectMany(s => s.Images)
            .Where(i => i.IsSelected.HasValue)
            .ToDictionary(i => i.SopInstanceUid, i => i.IsSelected, StringComparer.Ordinal);
        var selected = ResolveSelection(available.Select(f => (f.SopInstanceUid, f.Source)), saved);
        if (selected.Count == 0) return null;

        var selectedPaths = available
            .Where(f => selected.Contains(f.SopInstanceUid))
            .Select(f => f.PngPath!)
            .ToList();
        if (selectedPaths.Count == 0) return null;

        var perPageInput = DefaultPerPageInput(study);
        var perPage = EffectivePerPage(perPageInput, selected.Count);
        var (pageSize, isBooklet) = ForMode(DefaultPrintMode);

        return new PreviewRequest(
            study.Patient?.PatientName ?? "",
            study.StudyDate?.ToString("dd/MM/yyyy") ?? "N/A",
            study.Description ?? "",
            selectedPaths,
            study.ResumePdfPath,
            pageSize,
            isBooklet,
            perPage,
            DefaultGapPx,
            DefaultMarginPx);
    }

    /// <summary>
    /// Saved operator choice wins (any IsSelected != null restores exactly that set);
    /// otherwise PRINT-only when mixed, all-selected when pure C-STORE.
    /// </summary>
    public static HashSet<string> ResolveSelection(
        IEnumerable<(string Sop, string Source)> available,
        IReadOnlyDictionary<string, bool?> saved)
    {
        var list = available.ToList();
        if (saved.Values.Any(v => v.HasValue))
        {
            var availableSops = new HashSet<string>(list.Select(x => x.Sop), StringComparer.Ordinal);
            return new HashSet<string>(
                saved.Where(kv => kv.Value == true && availableSops.Contains(kv.Key)).Select(kv => kv.Key),
                StringComparer.Ordinal);
        }
        var hasPrint = list.Any(x => x.Source == "PRINT");
        return new HashSet<string>(
            (hasPrint ? list.Where(x => x.Source == "PRINT") : list).Select(x => x.Sop),
            StringComparer.Ordinal);
    }

    /// <summary>"1" for PRINT/CT/OT studies, "" (= Auto) otherwise.</summary>
    public static string DefaultPerPageInput(Study study)
    {
        var onePerPage = study.Series.SelectMany(s => s.Images).Any(i => i.Source == "PRINT")
            || study.Series.Any(s =>
                string.Equals(s.Modality, "CT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Modality, "OT", StringComparison.OrdinalIgnoreCase));
        return onePerPage ? "1" : "";
    }

    public static int EffectivePerPage(string perPageInput, int selectedCount)
    {
        var mode = string.IsNullOrWhiteSpace(perPageInput)
            ? "Auto"
            : int.TryParse(perPageInput.Trim(), out var v) && v >= 1 && v <= 16 ? v.ToString() : "Auto";
        return mode == "Auto" ? ComputeAutoPerPage(Math.Max(1, selectedCount)) : int.Parse(mode);
    }

    public static int ComputeAutoPerPage(int count)
    {
        if (count <= 0) return 1;
        var pages = (int)Math.Ceiling(count / 6.0);
        return Math.Clamp((int)Math.Ceiling(count / (double)pages), 1, 16);
    }

    public static (string PageSize, bool IsBooklet) ForMode(string printMode) =>
        printMode switch
        {
            "FlatA3" => ("A3", false),
            "BookletA3" => ("A4", true),
            _ => ("A4", false),
        };
}
