using System.IO;
using System.Windows;
using FocusMed.Launcher.Services;

namespace FocusMed.Launcher.Windows;

public partial class ResumePickerWindow : Window
{
    private readonly DatabaseService _databaseService;
    private readonly string _pdfPath = "";
    private readonly string _resumesFolder;
    private List<StudyItem> _studies = new();

    public ResumePickerWindow(DatabaseService databaseService, string pdfPath, string resumesFolder = "resumes")
    {
        _databaseService = databaseService;
        _pdfPath = pdfPath;
        _resumesFolder = resumesFolder;
        InitializeComponent();
        Closed += (_, _) => { try { if (!string.IsNullOrEmpty(_pdfPath) && File.Exists(_pdfPath)) File.Delete(_pdfPath); } catch { } };
        _ = LoadStudiesAsync();
    }

    private async Task LoadStudiesAsync()
    {
        try
        {
            StatusText.Text = "Chargement...";
            var studies = ShowAllCheckbox?.IsChecked == true
                ? await _databaseService.GetAllStudiesAsync()
                : await _databaseService.GetSelectableStudiesAsync();

            _studies = studies.Select(s => new StudyItem
            {
                Id = s.Id,
                PatientName = string.IsNullOrWhiteSpace(s.Patient?.PatientName)
                    ? "Inconnu"
                    : s.Patient.PatientName.Replace("^", " "),
                StudyDate = s.StudyDate?.ToString("dd/MM/yyyy") ?? s.CreatedAt.ToString("dd/MM/yyyy"),
                Modality = s.Series.FirstOrDefault()?.Modality ?? "-",
                ImageCount = s.Series.SelectMany(se => se.Images).Count(),
                HasResume = !string.IsNullOrEmpty(s.ResumePdfPath)
            }).ToList();

            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erreur: {ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox?.Text?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(term)
            ? _studies
            : _studies.Where(s =>
                s.PatientName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Id.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
                || s.Modality.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();

        StudiesGrid.ItemsSource = filtered;

        StatusText.Text = filtered.Count > 0
            ? $"{filtered.Count} etude(s)"
            : "Aucune etude correspondante.";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private async void ShowAll_Changed(object sender, RoutedEventArgs e)
    {
        await LoadStudiesAsync();
    }

    private void StudiesGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = StudiesGrid.SelectedItem != null;
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (StudiesGrid.SelectedItem is not StudyItem selected) return;

        if (string.IsNullOrEmpty(_pdfPath) || !File.Exists(_pdfPath))
        {
            System.Windows.MessageBox.Show("Aucun document a associer.", "FocusMed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dataDir = Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
        var resumesDir = Path.Combine(dataDir, _resumesFolder);
        Directory.CreateDirectory(resumesDir);

        var destFileName = $"resume_{selected.Id}_{Guid.NewGuid():N}.pdf";
        var destPath = Path.Combine(resumesDir, destFileName);

        try
        {
            File.Copy(_pdfPath, destPath, true);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erreur de copie: {ex.Message}", "FocusMed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var relativePath = $"resumes/{destFileName}";

        try
        {
            var success = await _databaseService.AssignResumeAsync(selected.Id, relativePath);

            if (success)
            {
                StatusText.Text = $"Associe a {selected.PatientName}.";
                Close();
            }
            else
            {
                try { File.Delete(destPath); } catch { }
                StatusText.Text = "Erreur lors de l'association.";
            }
        }
        catch
        {
            try { File.Delete(destPath); } catch { }
            StatusText.Text = "Erreur lors de l'association.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private class StudyItem
    {
        public int Id { get; set; }
        public string PatientName { get; set; } = "";
        public string StudyDate { get; set; } = "";
        public string Modality { get; set; } = "";
        public int ImageCount { get; set; }
        public bool HasResume { get; set; }
    }
}
