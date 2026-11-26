using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

public sealed class SilentUpdateRunResult
{
    public bool Success { get; set; }
    public int DevicesInspected { get; set; }
    public int UpdatesFound { get; set; }
    public int UpdatesSucceeded { get; set; }
    public int UpdatesSkippedManual { get; set; }
    public bool RebootRequired { get; set; }
    public string LogFilePath { get; set; } = "";
    public bool AlertSent { get; set; }
    public string AlertMessage { get; set; } = "";
    public List<DriverUpdateFailureRecord> Failures { get; set; } = new();

    public string Summary => Success
        ? $"Silent update succeeded: {UpdatesSucceeded}/{UpdatesFound} installed across {DevicesInspected} inspected device(s)." +
          (RebootRequired ? " Reboot required." : "")
        : $"Silent update completed with {Failures.Count} failure(s) ({UpdatesSucceeded}/{UpdatesFound} installed). xSecurity alert: {(AlertSent ? "Sent" : "Failed")}.";
}

/// <summary>
/// Headless silent driver updater invoked by the Windows Scheduled Task (--scheduled-update)
/// or on-demand from the GUI. Checks all enabled sources, downloads and installs newer drivers
/// silently, writes detailed investigation logs, and alerts xServer xSecurity + all connected
/// devices if any update fails.
/// </summary>
public sealed class SilentDriverUpdateService
{
    private readonly HardwareDriverInspector _inspector = new();
    private readonly WindowsUpdateDriverService _wuaService = new();
    private readonly MicrosoftCatalogService _catalogService = new();
    private readonly DirectManufacturerService _directService = new();
    private readonly RepositoryManagerService _repoService = new();
    private readonly DriverStoreAndInstallService _storeService = new();
    private readonly XSecurityAlertService _alertService = new();
    private readonly DriverHistoryService _historyService = new();

    public async Task<SilentUpdateRunResult> RunSilentUpdateAsync(
        AppSettings settings,
        Action<string>? externalLog = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.LogDirectory);
        var runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var logFilePath = Path.Combine(settings.LogDirectory, $"scheduled-update-{runStamp}.log");
        var latestLogPath = Path.Combine(settings.LogDirectory, "scheduled-update-latest.log");
        var historyPath = Path.Combine(settings.LogDirectory, "scheduled-update-history.jsonl");

        var logBuffer = new StringBuilder();
        var logLock = new object();

        void Log(string msg)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            lock (logLock)
            {
                logBuffer.AppendLine(line);
                try { File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            externalLog?.Invoke(msg);
        }

        var result = new SilentUpdateRunResult { LogFilePath = logFilePath };
        var startedUtc = DateTime.UtcNow;

        Log($"=== xDriverUpdater Scheduled Silent Update Started on {Environment.MachineName} ===");
        Log($"Sources: WUA/Catalog={settings.IncludeWindowsUpdateChannel}, Direct={settings.IncludeDirectManufacturerChecks}, Repos={settings.IncludeCustomRepositories}");
        Log($"Safety: RestorePoint={settings.CreateRestorePointBeforeInstall}, AutoBackup={settings.BackupDriverBeforeInstall}, PreferLaptopOEM={settings.PreferOemDriversOnLaptops}");

        try
        {
            // 1. Inspect hardware & installed drivers
            var (profile, devices) = await _inspector.InspectSystemAsync(settings.PreferOemDriversOnLaptops, ct);
            result.DevicesInspected = devices.Count;
            Log($"Platform: {profile.PlatformSummary} | OS: {profile.OsCaption} (Build {profile.OsBuild}) | Devices: {devices.Count}");

            // 2. Scan enabled update sources
            var discovered = new List<DriverUpdateCandidate>();
            if (settings.IncludeWindowsUpdateChannel)
            {
                var (wuaList, wuaMsg) = await _wuaService.ScanDriverUpdatesAsync(devices, Log, ct);
                discovered.AddRange(wuaList);
                Log(wuaMsg);

                var catList = await _catalogService.QueryCatalogForPrimaryDevicesAsync(devices, Log, ct);
                discovered.AddRange(catList);
            }

            if (settings.IncludeDirectManufacturerChecks)
            {
                var directList = await _directService.CheckDirectManufacturerUpdatesAsync(profile, devices, settings.PreferOemDriversOnLaptops, Log, ct);
                discovered.AddRange(directList);
            }

            if (settings.IncludeCustomRepositories)
            {
                var repoList = await _repoService.ScanRepositoriesAsync(profile, devices, settings.Repositories, settings.PreferOemDriversOnLaptops, Log, ct);
                discovered.AddRange(repoList);
            }

            // 3. Pick the single highest-ranked newer candidate per device
            var newerCandidates = discovered
                .Where(u => u.IsNewerThanInstalled)
                .GroupBy(u => string.IsNullOrWhiteSpace(u.DeviceId) ? u.DeviceName : u.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(grp => grp
                    .OrderByDescending(v => DirectManufacturerService.ParseComparableVersion(v.Version))
                    .ThenByDescending(v => v.ReleaseDate ?? DateTime.MinValue)
                    .ThenByDescending(v => v.MatchScore)
                    .First())
                .ToList();

            var installable = new List<DriverUpdateCandidate>();
            foreach (var cand in newerCandidates)
            {
                if (!string.IsNullOrWhiteSpace(cand.DeviceId) &&
                    settings.PinnedDevices != null &&
                    settings.PinnedDevices.TryGetValue(cand.DeviceId, out var pinnedVer))
                {
                    Log($"[Pinned] Skipping {cand.DeviceName} (pinned at v{pinnedVer}); candidate v{cand.Version} ignored.");
                    continue;
                }

                if (cand.InstallerType.Equals("page", StringComparison.OrdinalIgnoreCase))
                {
                    result.UpdatesSkippedManual++;
                    Log($"[Skip] Manual-only vendor portal link skipped in silent mode: {cand.DeviceName} -> v{cand.Version} ({cand.SourceLabel})");
                    continue;
                }
                installable.Add(cand);
            }

            result.UpdatesFound = installable.Count;
            Log($"Newer silently installable updates found: {installable.Count} (manual-only skipped: {result.UpdatesSkippedManual}).");

            if (installable.Count > 0)
            {
                if (settings.CreateRestorePointBeforeInstall)
                    await _storeService.CreateSystemRestorePointAsync("xDriverUpdater Scheduled Auto-Update", Log);

                // 4. Install Windows Update (WUA) batch silently
                var wuaBatch = installable.Where(u => u.SourceKind == UpdateSourceKind.WindowsUpdate).ToList();
                if (wuaBatch.Count > 0)
                {
                    if (settings.BackupDriverBeforeInstall)
                    {
                        foreach (var w in wuaBatch)
                        {
                            var dev = devices.FirstOrDefault(d => d.DeviceId == w.DeviceId);
                            if (dev != null && !string.IsNullOrWhiteSpace(dev.InfName))
                                await _storeService.ExportSingleDriverBackupAsync(dev.InfName, settings.BackupDirectory, Log);
                        }
                    }

                    var (ok, reboot, msg) = await _wuaService.DownloadAndInstallWuaUpdatesAsync(wuaBatch, Log, ct);
                    Log($"[WUA] {msg}");
                    if (reboot) result.RebootRequired = true;
                    if (ok)
                    {
                        result.UpdatesSucceeded += wuaBatch.Count;
                    }
                    else
                    {
                        foreach (var w in wuaBatch)
                        {
                            result.Failures.Add(new DriverUpdateFailureRecord
                            {
                                DeviceName = w.DeviceName,
                                DeviceId = w.DeviceId,
                                InstalledVersion = w.InstalledVersion,
                                TargetVersion = w.Version,
                                SourceLabel = w.SourceLabel,
                                Title = w.Title,
                                Error = msg
                            });
                        }
                    }
                }

                // 5. Install non-WUA updates (MS Catalog CAB, Direct NVIDIA/vendor silent EXE/MSI, Custom Repos)
                var nonWua = installable.Where(u => u.SourceKind != UpdateSourceKind.WindowsUpdate).ToList();
                foreach (var cand in nonWua)
                {
                    ct.ThrowIfCancellationRequested();
                    Log($"[Install] Starting silent install: {cand.DeviceName} ({cand.InstalledVersion} -> v{cand.Version}) via {cand.SourceLabel}...");
                    try
                    {
                        var dev = devices.FirstOrDefault(d => d.DeviceId == cand.DeviceId);
                        var (ok, reboot, msg) = await _storeService.InstallCandidateAsync(
                            cand, dev, settings, _wuaService, Log, progress: null, ct: ct, silentMode: true);
                        Log($"[Install] [{cand.DeviceName}] {(ok ? "SUCCESS" : "FAILED")}: {msg}");
                        _historyService.Record(new DriverHistoryEntry
                        {
                            Action = "ScheduledInstall",
                            DeviceName = cand.DeviceName,
                            DeviceId = cand.DeviceId,
                            FromVersion = cand.InstalledVersion,
                            ToVersion = cand.Version,
                            Source = cand.SourceLabel,
                            Success = ok,
                            Message = msg
                        });
                        if (reboot) result.RebootRequired = true;

                        if (ok)
                        {
                            result.UpdatesSucceeded++;
                        }
                        else
                        {
                            result.Failures.Add(new DriverUpdateFailureRecord
                            {
                                DeviceName = cand.DeviceName,
                                DeviceId = cand.DeviceId,
                                InstalledVersion = cand.InstalledVersion,
                                TargetVersion = cand.Version,
                                SourceLabel = cand.SourceLabel,
                                Title = cand.Title,
                                Error = msg
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Install] [{cand.DeviceName}] EXCEPTION: {ex.Message}");
                        result.Failures.Add(new DriverUpdateFailureRecord
                        {
                            DeviceName = cand.DeviceName,
                            DeviceId = cand.DeviceId,
                            InstalledVersion = cand.InstalledVersion,
                            TargetVersion = cand.Version,
                            SourceLabel = cand.SourceLabel,
                            Title = cand.Title,
                            Error = ex.Message
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Unhandled failure during scheduled silent driver update: {ex}");
            result.Failures.Add(new DriverUpdateFailureRecord
            {
                DeviceName = "Scheduled Update Engine",
                DeviceId = Environment.MachineName,
                InstalledVersion = "—",
                TargetVersion = "—",
                SourceLabel = "xDriverUpdater",
                Title = "Scheduled driver update pipeline exception",
                Error = ex.Message
            });
        }

        result.Success = result.Failures.Count == 0;
        Log($"=== Run Finished: Success={result.Success}, Installed={result.UpdatesSucceeded}/{result.UpdatesFound}, Failures={result.Failures.Count}, RebootRequired={result.RebootRequired} ===");

        // 6. On any failure: alert xServer xSecurity (which records event + broadcasts toast to all connected devices),
        //    trigger local Windows toast if interactive, and write to Windows Event Log.
        if (!result.Success)
        {
            var alertTitle = $"[{Environment.MachineName}] Driver Update Failed ({result.Failures.Count} failure(s))";
            var alertSummary = $"Scheduled silent driver update failed for {result.Failures.Count} item(s) ({result.UpdatesSucceeded}/{result.UpdatesFound} succeeded).";

            var (sent, alertMsg) = await _alertService.SendDriverUpdateFailureAlertAsync(
                settings, alertTitle, alertSummary, result.Failures, logFilePath, Log, ct);
            result.AlertSent = sent;
            result.AlertMessage = alertMsg;

            if (settings.EnableLocalToastOnFailure)
            {
                var toastBody = result.Failures.Count > 0
                    ? $"{result.Failures[0].DeviceName}: {result.Failures[0].Error}"
                    : alertSummary;
                XSecurityAlertService.TryShowLocalWindowsToast(alertTitle, toastBody, Log);
            }

            XSecurityAlertService.TryWriteWindowsEventLog(
                $"{alertTitle}\n{alertSummary}\nLog File: {logFilePath}\n\n" +
                string.Join("\n", result.Failures.Select(f => $"- {f.DeviceName} ({f.InstalledVersion} -> {f.TargetVersion}): {f.Error}")),
                EventLogEntryType.Error,
                Log);
        }
        else if (result.UpdatesSucceeded > 0)
        {
            XSecurityAlertService.TryWriteWindowsEventLog(
                $"xDriverUpdater silently installed {result.UpdatesSucceeded} driver update(s) on {Environment.MachineName}. Reboot required: {result.RebootRequired}. Log: {logFilePath}",
                EventLogEntryType.Information,
                Log);
        }

        // Persist latest log snapshot and structured JSONL history record
        try
        {
            File.WriteAllText(latestLogPath, logBuffer.ToString(), Encoding.UTF8);
            var historyEntry = new
            {
                startedUtc,
                completedUtc = DateTime.UtcNow,
                host = Environment.MachineName,
                success = result.Success,
                devicesInspected = result.DevicesInspected,
                updatesFound = result.UpdatesFound,
                updatesSucceeded = result.UpdatesSucceeded,
                updatesSkippedManual = result.UpdatesSkippedManual,
                rebootRequired = result.RebootRequired,
                alertSent = result.AlertSent,
                alertMessage = result.AlertMessage,
                logFilePath,
                failures = result.Failures
            };
            File.AppendAllText(historyPath, JsonSerializer.Serialize(historyEntry) + Environment.NewLine, Encoding.UTF8);
        }
        catch { }

        return result;
    }
}
