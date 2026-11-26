using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Queries the live Microsoft Update Catalog (catalog.update.microsoft.com) by Hardware ID
/// to retrieve real versioned WHQL driver packages (.cab) — including multiple historical versions
/// so users can choose the exact driver version to install or roll back to.
/// </summary>
public sealed class MicrosoftCatalogService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex RowRegex = new(
        @"<tr[^>]*id=""([0-9a-f\-]{36})_R\d+""[^>]*>(.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CellRegex = new(
        @"<td[^>]*>(.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex VersionInTitleRegex = new(
        @"\((\d+\.\d+\.\d+(?:\.\d+)?)\)|\b(\d+\.\d+\.\d+\.\d+)\b",
        RegexOptions.Compiled);

    private static readonly Regex CabUrlRegex = new(
        @"https?://[^'""\s<>]+\.cab",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static MicrosoftCatalogService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) xDriverUpdater/1.0");
    }

    public async Task<List<DriverUpdateCandidate>> QueryCatalogForPrimaryDevicesAsync(
        List<DriverDevice> devices,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var results = new List<DriverUpdateCandidate>();

        // Query key hardware categories (plus any problem or generic inbox devices) with concrete Hardware IDs
        var targetCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Display", "Display & Imaging", "Network", "Audio", "Bluetooth", "Storage & NVMe",
            "Chipset & System", "USB", "Security & Firmware"
        };

        var queriedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queryTargets = new List<(DriverDevice Device, string PrimaryHwid, string? SubsysHwid)>();

        foreach (var d in devices)
        {
            if (!targetCategories.Contains(d.Category) && !d.HasProblem && d.HealthStatus != DriverHealthStatus.InboxGeneric)
                continue;
            if (string.IsNullOrWhiteSpace(d.VendorId) || string.IsNullOrWhiteSpace(d.ProductId))
                continue;
            if (d.BusType is not ("PCI" or "USB" or "HDAUDIO" or "MONITOR"))
                continue;

            var baseHwid = d.BusType switch
            {
                "USB" => $"USB\\VID_{d.VendorId}&PID_{d.ProductId}",
                "MONITOR" => $"MONITOR\\{d.VendorId}{d.ProductId}",
                _ => $"{d.BusType}\\VEN_{d.VendorId}&DEV_{d.ProductId}"
            };

            string? subsysHwid = d.BusType is "PCI" or "HDAUDIO" && !string.IsNullOrWhiteSpace(d.SubsystemFull)
                ? $"{baseHwid}&SUBSYS_{d.SubsystemFull}"
                : null;

            if (queriedKeys.Add(subsysHwid ?? baseHwid))
                queryTargets.Add((d, baseHwid, subsysHwid));
        }

        // Run up to 4 concurrent catalog lookups so scanning stays fast
        using var sem = new SemaphoreSlim(4);
        var lockObj = new object();

        var tasks = queryTargets.Select(async target =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var found = new List<DriverUpdateCandidate>();
                // Prefer exact SUBSYS OEM Hardware ID first when available
                if (!string.IsNullOrWhiteSpace(target.SubsysHwid))
                {
                    var subsysMatches = await QueryHardwareIdAsync(target.Device, target.SubsysHwid, isExactSubsys: true, log, ct);
                    found.AddRange(subsysMatches);
                }

                // Also query base VEN+DEV if SUBSYS returned fewer than 2 versions
                if (found.Count < 2)
                {
                    var baseMatches = await QueryHardwareIdAsync(target.Device, target.PrimaryHwid, isExactSubsys: false, log, ct);
                    var existingVers = new HashSet<string>(found.Select(f => f.Version), StringComparer.OrdinalIgnoreCase);
                    foreach (var m in baseMatches)
                    {
                        if (existingVers.Add(m.Version))
                            found.Add(m);
                    }
                }

                if (found.Count > 0)
                {
                    lock (lockObj)
                    {
                        results.AddRange(found);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[MS Catalog] Query skipped for {target.Device.DisplayName}: {ex.Message}");
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<List<DriverUpdateCandidate>> QueryHardwareIdAsync(
        DriverDevice device,
        string hwidQuery,
        bool isExactSubsys,
        Action<string>? log,
        CancellationToken ct)
    {
        var list = new List<DriverUpdateCandidate>();
        var url = $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(hwidQuery)}";
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return list;

        var html = await resp.Content.ReadAsStringAsync(ct);
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match row in RowRegex.Matches(html))
        {
            var guid = row.Groups[1].Value;
            var cells = CellRegex.Matches(row.Groups[2].Value);
            if (cells.Count < 7) continue;

            var title = CleanHtml(cells[1].Groups[1].Value);
            var products = CleanHtml(cells[2].Groups[1].Value);
            var classification = CleanHtml(cells[3].Groups[1].Value);
            var lastUpdatedStr = CleanHtml(cells[4].Groups[1].Value);
            var versionCol = CleanHtml(cells[5].Groups[1].Value);
            var sizeStr = CleanHtml(cells[6].Groups[1].Value);

            if (!classification.Contains("Driver", StringComparison.OrdinalIgnoreCase))
                continue;

            // Filter out ARM64 / x86-only mismatches on 64-bit Windows when explicitly labeled
            if (title.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
                continue;

            var version = ExtractVersion(versionCol, title);
            if (string.IsNullOrWhiteSpace(version))
                continue;

            // Deduplicate identical versions from multi-OS rows (keep the newest Win11/Win10 match)
            if (!seenVersions.Add(version))
                continue;

            DateTime? relDate = DateTime.TryParse(lastUpdatedStr, out var dt) ? dt : null;
            long sizeBytes = ParseSize(sizeStr);
            bool isNewer = IsCatalogCandidateNewer(device, version, relDate);

            list.Add(new DriverUpdateCandidate
            {
                Id = $"mscat:{guid}",
                SourceKind = UpdateSourceKind.MicrosoftCatalog,
                SourceName = "Microsoft Update Catalog (WHQL CAB)",
                Title = title,
                Vendor = device.SiliconVendor,
                Version = version,
                ReleaseDate = relDate,
                SizeBytes = sizeBytes,
                DownloadUrl = $"https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid={guid}",
                CatalogUpdateGuid = guid,
                InstallerType = "ms-catalog-cab",
                Channel = "whql",
                ReleaseNotes = $"WHQL-signed Microsoft Update Catalog CAB package.\nProducts: {products}\nHardware ID Query: {hwidQuery}\nUpdate GUID: {guid}",
                MatchedHardwareId = hwidQuery,
                DeviceName = device.DisplayName,
                DeviceId = device.DeviceId,
                InstalledVersion = device.DriverVersion,
                IsNewerThanInstalled = isNewer,
                IsOemSpecific = isExactSubsys,
                MatchScore = isNewer
                    ? (isExactSubsys ? 96 : (device.IsLaptopOemCustomized ? 92 : 84))
                    : 45
            });

            // Keep up to 8 distinct versions per device for clean version selection
            if (list.Count >= 8) break;
        }

        if (list.Count > 0)
            log?.Invoke($"[MS Catalog] {device.DisplayName} ({hwidQuery}): found {list.Count} WHQL version(s) (latest {list[0].Version}).");

        return list;
    }

    /// <summary>
    /// Resolves the actual direct .cab download URL from Microsoft Update Catalog's DownloadDialog endpoint.
    /// </summary>
    public async Task<string?> ResolveCabDownloadUrlAsync(string updateGuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(updateGuid)) return null;
        var payload = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateGuid}\",\"updateID\":\"{updateGuid}\"}}]";
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("updateIDs", payload)
        });

        using var resp = await Http.PostAsync("https://www.catalog.update.microsoft.com/DownloadDialog.aspx", content, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var html = await resp.Content.ReadAsStringAsync(ct);
        var m = CabUrlRegex.Match(html);
        return m.Success ? m.Value : null;
    }

    private static bool IsCatalogCandidateNewer(DriverDevice device, string candidateVersion, DateTime? catalogDate)
    {
        if (!DirectManufacturerService.IsVersionNewer(candidateVersion, device.DriverVersion))
            return false;

        // Windows PnP ranks drivers by DriverDate before DriverVersion, and vendors such as
        // Realtek ship parallel branches with different major version schemes (e.g. NDIS 10.x
        // vs NetAdapterCx 1168.x). Never treat an older catalog package as a newer update.
        bool hasRealInstalledDate = device.DriverDate.HasValue &&
            !(device.DriverDate.Value.Year == 2006 && device.DriverDate.Value.Month == 6 && device.DriverDate.Value.Day == 21);

        if (hasRealInstalledDate && catalogDate.HasValue && catalogDate.Value.Date < device.DriverDate!.Value.Date)
            return false;

        var candParsed = DirectManufacturerService.ParseComparableVersion(candidateVersion);
        var instParsed = DirectManufacturerService.ParseComparableVersion(device.DriverVersion);
        bool crossBranchMajorJump = instParsed.Major > 0 && instParsed.Major < 100 && candParsed.Major >= 100;
        if (crossBranchMajorJump)
        {
            if (!hasRealInstalledDate || !catalogDate.HasValue)
                return false;
            return catalogDate.Value.Date > device.DriverDate!.Value.Date;
        }

        return true;
    }

    private static string CleanHtml(string raw)
    {
        var noTags = Regex.Replace(raw, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string ExtractVersion(string versionCol, string title)
    {
        if (!string.IsNullOrWhiteSpace(versionCol) && Regex.IsMatch(versionCol, @"^\d+\.\d+"))
            return versionCol.Trim();

        var m = VersionInTitleRegex.Match(title);
        if (m.Success)
            return !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;

        return "";
    }

    private static long ParseSize(string sizeStr)
    {
        var m = Regex.Match(sizeStr ?? "", @"([\d\.]+)\s*(GB|MB|KB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val))
            return 0;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "GB" => (long)(val * 1024 * 1024 * 1024),
            "MB" => (long)(val * 1024 * 1024),
            "KB" => (long)(val * 1024),
            _ => 0
        };
    }
}
