using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ScanWithWeb.Services;

internal sealed class UserPreferences
{
    public string? DefaultPrinter { get; set; }
    public string? DefaultScannerId { get; set; }
    public string? DefaultScannerProtocol { get; set; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string GetFilePath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScanWithWeb");
        return Path.Combine(appDataPath, "user-settings.json");
    }

    public static UserPreferences Load(ILogger logger)
    {
        var path = GetFilePath();
        try
        {
            if (!File.Exists(path))
            {
                return new UserPreferences();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserPreferences>(json, SerializerOptions) ?? new UserPreferences();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load user preferences from {Path}", path);
            return new UserPreferences();
        }
    }

    public void Save(ILogger logger)
    {
        var path = GetFilePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(path, json);
            logger.LogInformation("Saved user preferences to {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save user preferences to {Path}", path);
        }
    }
}
