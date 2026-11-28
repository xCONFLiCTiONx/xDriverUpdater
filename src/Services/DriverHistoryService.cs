using System.IO;
using System.Text.Json;

namespace xDriverUpdater.Services;

public sealed class DriverHistoryEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = "Install"; // Install, ScheduledInstall, Rollback, LocalInstall
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string BackupPath { get; set; } = "";

    public string TimeText => TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Persists driver installation, scheduled update, and rollback history
/// so users have a complete audit trail and can inspect or revert changes.
/// </summary>
public sealed class DriverHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _historyPath;
    private readonly object _lock = new();

    public DriverHistoryService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "xPrograms");
        var dir = Path.Combine(baseDir, "xSecurityCenter");
        _historyPath = Path.Combine(dir, "history.json");

        foreach (var legacyFolder in new[] { "xSystemForge", "xDriverUpdater" })
        {
            var legacy = Path.Combine(baseDir, legacyFolder, "history.json");
            if (!File.Exists(_historyPath) && File.Exists(legacy))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(legacy, _historyPath, overwrite: false);
                }
                catch { }
            }
        }
    }

    public List<DriverHistoryEntry> LoadHistory()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_historyPath))
                    return new List<DriverHistoryEntry>();
                var json = File.ReadAllText(_historyPath);
                return JsonSerializer.Deserialize<List<DriverHistoryEntry>>(json, JsonOpts) ?? new List<DriverHistoryEntry>();
            }
            catch
            {
                return new List<DriverHistoryEntry>();
            }
        }
    }

    public void Record(DriverHistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                var list = LoadHistory();
                list.Insert(0, entry);
                if (list.Count > 250)
                    list = list.Take(250).ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(list, JsonOpts));
            }
            catch { }
        }
    }
}
