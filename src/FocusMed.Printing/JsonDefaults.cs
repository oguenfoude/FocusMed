using System.Text.Json;

namespace FocusMed.Printing;

public static class JsonDefaults
{
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true
    };
}
