using AnythingSearch.Helper;
using AnythingSearch.Models;
using DeviceDataModule;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnythingSearch.Services
{
    /// <summary>
    /// Persists AppSettings as appsettings.json in the app-data directory
    /// (ApplicationDataManager). Everything reads/writes the single Current
    /// instance and calls Save() — loads are cached, writes are serialized
    /// under a lock. Use ResetToDefaults()/Reload() for the Settings tab's
    /// Reset button and external edits respectively.
    /// </summary>
    public static class SettingsService
    {
        public static readonly string SettingsFilePath;

        private static readonly object Lock = new();
        private static AppSettings _current;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        static SettingsService()
        {
            string appDataDir = ApplicationDataManager.Instance.ApplicationDataDirectory;
            SettingsFilePath = Path.Combine(appDataDir, "appsettings.json");
            BackupFilePath = SettingsFilePath + ".bak";
        }

        /// <summary>
        /// One-generation backup of the previous good settings file, written
        /// before every save and consulted by Load() when the main file is
        /// corrupt — so a truncated write can never silently reset all user
        /// settings (H-7).
        /// </summary>
        private static readonly string BackupFilePath;

        /// <summary>The current settings — loaded on first access (thread-safe singleton).</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null)
                {
                    lock (Lock)
                    {
                        _current ??= Load();
                    }
                }
                return _current;
            }
        }

        /// <summary>Reads the file fresh (ignores the cache). Prefer Current.</summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading settings: {ex.Message}");

                // Main file unreadable — fall back to the last good backup
                // before giving up and returning defaults.
                try
                {
                    if (File.Exists(BackupFilePath))
                    {
                        var restored = JsonSerializer.Deserialize<AppSettings>(
                            File.ReadAllText(BackupFilePath), JsonOptions);
                        if (restored != null)
                        {
                            Logger.Log("Settings restored from backup.");
                            return restored;
                        }
                    }
                }
                catch (Exception backupEx)
                {
                    Logger.Log($"Backup restore also failed: {backupEx.Message}");
                }
            }

            return new AppSettings();
        }

        /// <summary>Saves the current settings to disk (atomically, with a one-generation backup).</summary>
        public static void Save()
        {
            lock (Lock)
            {
                try
                {
                    // Keep a copy of the previous good file for Load()'s
                    // fallback before swapping in the new content.
                    if (File.Exists(SettingsFilePath))
                        File.Copy(SettingsFilePath, BackupFilePath, overwrite: true);

                    AtomicFile.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Current, JsonOptions));
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error saving settings: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>Drops the cache and re-reads the file (e.g. after an external edit).</summary>
        public static void Reload()
        {
            lock (Lock)
            {
                _current = Load();
            }
        }

        /// <summary>Replaces the settings with a fresh default instance and saves it.</summary>
        public static void ResetToDefaults()
        {
            lock (Lock)
            {
                _current = new AppSettings();
                Save();
            }
        }

        /// <summary>Copies the settings file to a timestamped (or given) backup.</summary>
        public static bool CreateBackup(string backupFileName = null)
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return false;

                string backupName = backupFileName ?? $"appsettings_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string backupPath = Path.Combine(
                    ApplicationDataManager.Instance.ApplicationDataDirectory, backupName);
                File.Copy(SettingsFilePath, backupPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error creating backup: {ex.Message}");
                return false;
            }
        }

        /// <summary>Restores a backup file and reloads it.</summary>
        public static bool RestoreFromBackup(string backupFilePath)
        {
            try
            {
                if (!File.Exists(backupFilePath))
                    return false;

                File.Copy(backupFilePath, SettingsFilePath, true);
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error restoring backup: {ex.Message}");
                return false;
            }
        }
    }
}
