using System.Runtime.InteropServices;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Queries, downloads, and installs WHQL drivers through the exact same
/// Windows Update Agent (WUA) COM engine that Windows Device Manager uses.
/// </summary>
public sealed class WindowsUpdateDriverService
{
    public Task<(List<DriverUpdateCandidate> Updates, string StatusMessage)> ScanDriverUpdatesAsync(
        List<DriverDevice> devices,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var results = new List<DriverUpdateCandidate>();
            try
            {
                ct.ThrowIfCancellationRequested();
                log?.Invoke("[WUA] Creating Windows Update Agent session (Device Manager channel)...");

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (sessionType == null)
                    return (results, "Windows Update Agent COM interface is not available on this system.");

                dynamic session = Activator.CreateInstance(sessionType)!;
                session.ClientApplicationID = "xDriverUpdater";

                dynamic searcher = session.CreateUpdateSearcher();
                // 2 = ssWindowsUpdate (Windows Update / Microsoft Update online)
                try { searcher.ServerSelection = 2; } catch { }
                searcher.Online = true;

                log?.Invoke("[WUA] Searching Microsoft Windows Update for matching Hardware-ID drivers...");
                dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

                int count = (int)searchResult.Updates.Count;
                log?.Invoke($"[WUA] Found {count} driver update(s) from Windows Update.");

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic upd = searchResult.Updates.Item(i);

                    string title = SafeStr(() => (string)upd.Title);
                    string driverModel = SafeStr(() => (string)upd.DriverModel);
                    string driverProvider = SafeStr(() => (string)upd.DriverProvider);
                    string driverClass = SafeStr(() => (string)upd.DriverClass);
                    string driverHwid = SafeStr(() => (string)upd.DriverHardwareID);
                    string description = SafeStr(() => (string)upd.Description);
                    string updateId = SafeStr(() => (string)upd.Identity.UpdateID);
                    int revNum = SafeInt(() => (int)upd.Identity.RevisionNumber);
                    long maxSize = SafeLong(() => Convert.ToInt64(upd.MaxDownloadSize));
                    bool rebootReq = SafeBool(() => (bool)upd.RebootRequired);
                    DateTime? verDate = SafeDate(() => (DateTime)upd.DriverVerDate);

                    // Match back to our inspected device list
                    var matchedDevice = MatchDevice(devices, driverHwid, driverModel, title, driverProvider);

                    var version = ExtractVersionFromTitle(title);
                    if (string.IsNullOrWhiteSpace(version) && verDate.HasValue)
                        version = verDate.Value.ToString("yyyy.MM.dd");

                    var candidate = new DriverUpdateCandidate
                    {
                        SourceKind = UpdateSourceKind.WindowsUpdate,
                        SourceName = "Windows Update (Device Manager)",
                        Title = string.IsNullOrWhiteSpace(title) ? driverModel : title,
                        Vendor = string.IsNullOrWhiteSpace(driverProvider) ? "Microsoft / WHQL" : driverProvider,
                        Version = version,
                        ReleaseDate = verDate,
                        SizeBytes = maxSize,
                        InstallerType = "wua",
                        Channel = "whql",
                        ReleaseNotes = string.IsNullOrWhiteSpace(description)
                            ? $"WHQL-signed driver from Windows Update. Hardware ID: {driverHwid}"
                            : description,
                        MatchedHardwareId = driverHwid,
                        RebootRequired = rebootReq,
                        WuaUpdateId = updateId,
                        WuaRevisionNumber = revNum,
                        DeviceName = matchedDevice?.DisplayName ?? (string.IsNullOrWhiteSpace(driverModel) ? title : driverModel),
                        DeviceId = matchedDevice?.DeviceId ?? "",
                        InstalledVersion = matchedDevice?.DriverVersion ?? "Unknown",
                        MatchScore = matchedDevice?.IsLaptopOemCustomized == true ? 95 : 80
                    };

                    results.Add(candidate);
                    if (matchedDevice != null)
                    {
                        matchedDevice.AvailableUpdates.Add(candidate);
                        if (!matchedDevice.HasProblem)
                            matchedDevice.HealthStatus = DriverHealthStatus.UpdateAvailable;
                    }
                }

                return (results, $"Windows Update scan complete: {results.Count} driver update(s) available.");
            }
            catch (COMException comEx)
            {
                var msg = $"Windows Update COM error (0x{comEx.HResult:X8}): {comEx.Message}";
                log?.Invoke($"[WUA] {msg}");
                return (results, msg);
            }
            catch (OperationCanceledException)
            {
                return (results, "Windows Update scan canceled.");
            }
            catch (Exception ex)
            {
                var msg = $"Windows Update scan error: {ex.Message}";
                log?.Invoke($"[WUA] {msg}");
                return (results, msg);
            }
        }, ct);
    }

    public Task<(bool Success, bool RebootRequired, string Message)> DownloadAndInstallWuaUpdatesAsync(
        List<DriverUpdateCandidate> wuaCandidates,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (wuaCandidates.Count == 0)
                return (true, false, "No Windows Update drivers selected.");

            try
            {
                var targetIds = new HashSet<string>(
                    wuaCandidates.Select(c => c.WuaUpdateId).Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);

                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")!;
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;

                dynamic session = Activator.CreateInstance(sessionType)!;
                session.ClientApplicationID = "xDriverUpdater";

                dynamic searcher = session.CreateUpdateSearcher();
                try { searcher.ServerSelection = 2; } catch { }
                searcher.Online = true;

                log?.Invoke("[WUA] Resolving selected updates from Windows Update Agent...");
                dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

                dynamic toDownload = Activator.CreateInstance(collType)!;
                for (int i = 0; i < (int)searchResult.Updates.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic upd = searchResult.Updates.Item(i);
                    string id = SafeStr(() => (string)upd.Identity.UpdateID);
                    if (targetIds.Contains(id))
                    {
                        try
                        {
                            if ((bool)upd.EulaAccepted == false)
                                upd.AcceptEula();
                        }
                        catch { }
                        toDownload.Add(upd);
                    }
                }

                if ((int)toDownload.Count == 0)
                    return (false, false, "Selected Windows Update driver(s) are no longer offered by Windows Update.");

                log?.Invoke($"[WUA] Downloading {(int)toDownload.Count} driver package(s)...");
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = toDownload;
                dynamic dlResult = downloader.Download();
                int dlCode = (int)dlResult.ResultCode;
                log?.Invoke($"[WUA] Download result code: {FormatResultCode(dlCode)}");

                dynamic toInstall = Activator.CreateInstance(collType)!;
                for (int i = 0; i < (int)toDownload.Count; i++)
                {
                    dynamic upd = toDownload.Item(i);
                    if (SafeBool(() => (bool)upd.IsDownloaded))
                        toInstall.Add(upd);
                }

                if ((int)toInstall.Count == 0)
                    return (false, false, $"Windows Update download failed ({FormatResultCode(dlCode)}).");

                log?.Invoke($"[WUA] Installing {(int)toInstall.Count} WHQL driver package(s)...");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = toInstall;
                dynamic instResult = installer.Install();

                int instCode = (int)instResult.ResultCode;
                bool reboot = SafeBool(() => (bool)instResult.RebootRequired);
                bool ok = instCode is 2 or 3; // 2 = Succeeded, 3 = SucceededWithErrors

                var msg = $"Windows Update install finished: {FormatResultCode(instCode)}" +
                          (reboot ? " (Reboot required)" : "");
                log?.Invoke($"[WUA] {msg}");
                return (ok, reboot, msg);
            }
            catch (Exception ex)
            {
                var msg = $"Windows Update install error: {ex.Message}";
                log?.Invoke($"[WUA] {msg}");
                return (false, false, msg);
            }
        }, ct);
    }

    private static DriverDevice? MatchDevice(
        List<DriverDevice> devices,
        string hwid,
        string driverModel,
        string title,
        string provider)
    {
        if (!string.IsNullOrWhiteSpace(hwid))
        {
            var exact = devices.FirstOrDefault(d =>
                d.HardwareIds.Any(h => h.Equals(hwid, StringComparison.OrdinalIgnoreCase)) ||
                d.HardwareId.Equals(hwid, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var prefix = devices.FirstOrDefault(d =>
                d.HardwareIds.Any(h => h.StartsWith(hwid, StringComparison.OrdinalIgnoreCase) ||
                                       hwid.StartsWith(h, StringComparison.OrdinalIgnoreCase)));
            if (prefix != null) return prefix;
        }

        if (!string.IsNullOrWhiteSpace(driverModel))
        {
            var byModel = devices.FirstOrDefault(d =>
                d.DisplayName.Contains(driverModel, StringComparison.OrdinalIgnoreCase) ||
                driverModel.Contains(d.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (byModel != null) return byModel;
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            var byTitle = devices.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.DisplayName) &&
                title.Contains(d.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (byTitle != null) return byTitle;
        }

        return null;
    }

    private static string ExtractVersionFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(title, @"\b(\d+\.\d+\.\d+(?:\.\d+)?)\b");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string FormatResultCode(int code) => code switch
    {
        0 => "NotStarted",
        1 => "InProgress",
        2 => "Succeeded",
        3 => "SucceededWithErrors",
        4 => "Failed",
        5 => "Aborted",
        _ => $"Code {code}"
    };

    private static string SafeStr(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
    private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    private static DateTime? SafeDate(Func<DateTime> f) { try { var d = f(); return d.Year > 1990 ? d : null; } catch { return null; } }
}
