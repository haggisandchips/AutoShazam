using System.IO;
using System.Text.Json;
using AutoShazam.Models;

namespace AutoShazam.Services.Settings;

public sealed class SettingsService
{
    private readonly string _filePath;

    public SettingsService(string appDataRoot)
    {
        _filePath = Path.Combine(appDataRoot, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults rather than crash.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best-effort persistence only.
        }
    }
}
