using System.Text.Json;
using AnythingSearch.Models;

namespace AnythingSearch.Services;

public class SettingsManager
{
    private readonly string _settingsPath;
    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public SettingsManager()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnythingSearch");

        Directory.CreateDirectory(appData);
        _settingsPath = Path.Combine(appData, "settings.json");

        _settings = Load();
    }

    private AppSettings Load()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    /// <summary>
    /// Marks the first run as complete and saves settings.
    /// Call this after the initial indexing is complete.
    /// </summary>
    public void MarkFirstRunComplete()
    {
        _settings.IsFirstRun = false;
        Save();
    }
}