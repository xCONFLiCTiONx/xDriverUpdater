using System.IO;
using System.Text.Json;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class TelemetrySnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string SerialNumber { get; set; } = "";
    public int DiskIndex { get; set; }
    public int? LifeRemainingPercent { get; set; }
    public int? AvailableSparePercent { get; set; }
    public long? ReallocatedSectors { get; set; }
    public long? PendingSectors { get; set; }
    public ulong? MediaErrors { get; set; }
    public long? UncorrectableErrors { get; set; }
    public ulong? TotalBytesWritten { get; set; }
    public long? PowerOnHours { get; set; }
    public int? TemperatureC { get; set; }
}

public sealed class HistoricalTrend
{
    public long ReallocatedSectorsDelta { get; set; }
    public long PendingSectorsDelta { get; set; }
    public ulong MediaErrorsDelta { get; set; }
    public int WearPercentDelta { get; set; }
    public double DaysBetween { get; set; }
    public double? DailyWearRatePercent { get; set; }
    public int? ProjectedDaysUntilEnduranceExhausted { get; set; }
}

public sealed class TelemetryHistoryManager
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "xPrograms", "xSecurityCenter");
    private static readonly string HistoryFile = Path.Combine(Folder, "drive-telemetry-history.json");
    private static readonly string LegacyHistoryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveSentinel", "telemetry-history.json");

    private readonly object _lock = new();
    private List<TelemetrySnapshot> _snapshots = new();

    public static TelemetryHistoryManager Instance { get; } = new();

    private TelemetryHistoryManager()
    {
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryFile) && File.Exists(LegacyHistoryFile))
            {
                Directory.CreateDirectory(Folder);
                File.Copy(LegacyHistoryFile, HistoryFile, overwrite: false);
            }
            if (File.Exists(HistoryFile))
            {
                var json = File.ReadAllText(HistoryFile);
                _snapshots = JsonSerializer.Deserialize<List<TelemetrySnapshot>>(json) ?? new();
            }
        }
        catch
        {
            _snapshots = new();
        }
    }

    public void Record(PhysicalDriveInfo drive)
    {
        if (string.IsNullOrWhiteSpace(drive.SerialNumber) || drive.SerialNumber.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var last = _snapshots
                .Where(s => s.SerialNumber.Equals(drive.SerialNumber, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefault();

            var shouldRecord = last == null ||
                (now - last.Timestamp).TotalHours >= 3 ||
                last.ReallocatedSectors != drive.ReallocatedSectors ||
                last.PendingSectors != drive.PendingSectors ||
                last.MediaErrors != drive.MediaErrors ||
                last.LifeRemainingPercent != drive.LifeRemainingPercent;

            if (shouldRecord)
            {
                _snapshots.Add(new TelemetrySnapshot
                {
                    Timestamp = now,
                    SerialNumber = drive.SerialNumber,
                    DiskIndex = drive.DiskIndex,
                    LifeRemainingPercent = drive.LifeRemainingPercent,
                    AvailableSparePercent = drive.AvailableSparePercent,
                    ReallocatedSectors = drive.ReallocatedSectors,
                    PendingSectors = drive.PendingSectors,
                    MediaErrors = drive.MediaErrors,
                    UncorrectableErrors = drive.UncorrectableErrors,
                    TotalBytesWritten = drive.TotalBytesWritten,
                    PowerOnHours = drive.PowerOnHours,
                    TemperatureC = drive.TemperatureC
                });

                var cutoff = now.AddDays(-180);
                _snapshots.RemoveAll(s => s.Timestamp < cutoff);
                Save();
            }
        }
    }

    public HistoricalTrend CalculateTrend(PhysicalDriveInfo drive)
    {
        var trend = new HistoricalTrend();
        if (string.IsNullOrWhiteSpace(drive.SerialNumber)) return trend;

        lock (_lock)
        {
            var history = _snapshots
                .Where(s => s.SerialNumber.Equals(drive.SerialNumber, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Timestamp)
                .ToList();

            if (history.Count < 2) return trend;

            var current = history.Last();
            var baseline = history.FirstOrDefault(s => (current.Timestamp - s.Timestamp).TotalDays >= 7) ?? history.First();

            var days = (current.Timestamp - baseline.Timestamp).TotalDays;
            if (days < 0.1) return trend;

            trend.DaysBetween = days;
            trend.ReallocatedSectorsDelta = (current.ReallocatedSectors ?? 0) - (baseline.ReallocatedSectors ?? 0);
            trend.PendingSectorsDelta = (current.PendingSectors ?? 0) - (baseline.PendingSectors ?? 0);
            trend.MediaErrorsDelta = (current.MediaErrors ?? 0) - (baseline.MediaErrors ?? 0);

            if (current.LifeRemainingPercent.HasValue && baseline.LifeRemainingPercent.HasValue)
            {
                var wearLoss = baseline.LifeRemainingPercent.Value - current.LifeRemainingPercent.Value;
                if (wearLoss > 0 && days >= 1.0)
                {
                    trend.WearPercentDelta = wearLoss;
                    var dailyRate = wearLoss / days;
                    trend.DailyWearRatePercent = dailyRate;
                    if (dailyRate > 0 && current.LifeRemainingPercent.Value > 0)
                        trend.ProjectedDaysUntilEnduranceExhausted = (int)Math.Ceiling(current.LifeRemainingPercent.Value / dailyRate);
                }
            }
        }

        return trend;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tempPath = HistoryFile + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_snapshots, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, HistoryFile, overwrite: true);
        }
        catch { }
    }
}
