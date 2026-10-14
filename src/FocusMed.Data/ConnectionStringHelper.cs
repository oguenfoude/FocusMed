namespace FocusMed.Data;

/// <summary>
/// Single connection-string precedence shared by the Worker host and the
/// design-time factory: explicit config value wins, then FOCUSMED_DB_CONNECTION
/// env (what the Launcher exports), then the LocalAppData default.
/// (The design-time factory has no config source, so it passes null.)
/// </summary>
public static class ConnectionStringHelper
{
    public static string Resolve(string? configuredValue)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue)) return configuredValue;
        var env = Environment.GetEnvironmentVariable("FOCUSMED_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return $@"Data Source={Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusMed", "focusmed.db")}";
    }
}
