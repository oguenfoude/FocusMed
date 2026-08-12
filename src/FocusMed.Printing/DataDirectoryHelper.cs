namespace FocusMed.Printing;

public static class DataDirectoryHelper
{
    public static string GetDataDirectory()
    {
        return Environment.GetEnvironmentVariable("FOCUSMED_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed");
    }
}
