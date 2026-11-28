using System.IO;
using System.Text.Json;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string ConfigDir { get; }
    public string ConfigPath { get; }

    public ConfigStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xPrograms");
        ConfigDir = Path.Combine(baseDir, "xSecurityCenter");
        ConfigPath = Path.Combine(ConfigDir, "settings.json");

        // Seamlessly migrate settings from legacy xSystemForge or xDriverUpdater folders on first launch
        if (!File.Exists(ConfigPath))
        {
            foreach (var legacyFolder in new[] { "xSystemForge", "xDriverUpdater" })
            {
                var legacyPath = Path.Combine(baseDir, legacyFolder, "settings.json");
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        Directory.CreateDirectory(ConfigDir);
                        File.Copy(legacyPath, ConfigPath, overwrite: false);
                        break;
                    }
                    catch { }
                }
            }
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    EnsureDefaults(loaded);
                    return loaded;
                }
            }
        }
        catch { }

        var fresh = new AppSettings();
        EnsureDefaults(fresh);
        Save(fresh);
        return fresh;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }

    private void EnsureDefaults(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.BackupDirectory))
            s.BackupDirectory = Path.Combine(ConfigDir, "Backups");

        if (string.IsNullOrWhiteSpace(s.DownloadCacheDirectory))
            s.DownloadCacheDirectory = Path.Combine(ConfigDir, "Downloads");

        if (string.IsNullOrWhiteSpace(s.LogDirectory))
            s.LogDirectory = Path.Combine(ConfigDir, "Logs");

        if (string.IsNullOrWhiteSpace(s.ScheduledTaskFrequency))
            s.ScheduledTaskFrequency = "Daily";

        if (string.IsNullOrWhiteSpace(s.ScheduledTaskTime))
            s.ScheduledTaskTime = "03:00";

        if (s.XSecurityEndpointUrl == null)
            s.XSecurityEndpointUrl = "";

        if (s.PinnedDevices == null)
            s.PinnedDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        else
            s.PinnedDevices = new Dictionary<string, string>(s.PinnedDevices, StringComparer.OrdinalIgnoreCase);

        if (s.Repositories == null)
            s.Repositories = new List<RepositorySourceConfig>();

        if (!s.Repositories.Any(r => r.IsBuiltIn))
        {
            s.Repositories.Insert(0, new RepositorySourceConfig
            {
                Id = "builtin-core",
                Name = "xDriverUpdater Core Vendor Catalog (Built-in)",
                UrlOrPath = "builtin://core",
                Enabled = true,
                IsBuiltIn = true,
                Description = "Curated baseline hardware-ID mappings for common NVIDIA, AMD, Intel, Realtek, Focusrite, and OEM drivers. Add your own JSON URLs or local folders below to extend capability!"
            });
        }

        // Seamlessly migrate legacy xPowerPlan config if not yet populated
        if (s.PowerPlanLastConfig == null && !s.PowerPlanManagedSchemeGuid.HasValue)
        {
            TryMigrateLegacyPowerPlan(s);
        }
    }

    private sealed class LegacyPowerPlanState
    {
        public Guid? ManagedSchemeGuid { get; set; }
        public PowerPlanConfig? LastConfig { get; set; }
        public DateTime? LastAppliedUtc { get; set; }
    }

    private static void TryMigrateLegacyPowerPlan(AppSettings s)
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "xPowerPlan", "config.json");
            if (!File.Exists(legacyPath)) return;
            var json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<LegacyPowerPlanState>(json, JsonOpts);
            if (legacy == null) return;
            s.PowerPlanManagedSchemeGuid = legacy.ManagedSchemeGuid;
            s.PowerPlanLastConfig = legacy.LastConfig;
            s.PowerPlanLastAppliedUtc = legacy.LastAppliedUtc;
        }
        catch { }
    }
}
