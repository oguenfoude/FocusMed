namespace FocusMed.Worker;

public static class PathHelper
{
    public static string GetDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("FOCUSMED_DATA");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "FocusMed");
    }
}
