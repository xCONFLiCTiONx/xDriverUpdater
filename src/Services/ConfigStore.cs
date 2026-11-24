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
        ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xPrograms", "xDriverUpdater");
        ConfigPath = Path.Combine(ConfigDir, "settings.json");
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
    }
}
