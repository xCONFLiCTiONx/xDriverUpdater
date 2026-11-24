using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Queries live direct manufacturer endpoints (NVIDIA dynamic PFID + multi-version Game Ready & Studio API)
/// returning multiple real versions so users can choose the exact version to install or roll back to.
/// </summary>
public sealed class DirectManufacturerService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static DirectManufacturerService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("xDriverUpdater/1.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<List<DriverUpdateCandidate>> CheckDirectManufacturerUpdatesAsync(
        SystemProfile profile,
        List<DriverDevice> devices,
        bool preferOemOnLaptops,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var results = new List<DriverUpdateCandidate>();

        var nvidiaGpus = devices
            .Where(d => d.Category == "Display" && d.SiliconVendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var gpu in nvidiaGpus)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var nvVersions = await QueryNvidiaMultiVersionAsync(profile, gpu, preferOemOnLaptops, log, ct);
                results.AddRange(nvVersions);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Direct:NVIDIA] Lookup skipped: {ex.Message}");
            }
        }

        return results;
    }

    private static async Task<List<DriverUpdateCandidate>> QueryNvidiaMultiVersionAsync(
        SystemProfile profile,
        DriverDevice gpu,
        bool preferOemOnLaptops,
        Action<string>? log,
        CancellationToken ct)
    {
        var candidates = new List<DriverUpdateCandidate>();
        bool isNotebook = profile.IsLaptop || gpu.DisplayName.Contains("Laptop", StringComparison.OrdinalIgnoreCase);
        int psid = DetectNvidiaSeriesId(gpu.DisplayName, isNotebook);
        int pfid = await ResolveNvidiaPfidAsync(psid, gpu.DisplayName, ct);

        if (pfid <= 0)
        {
            log?.Invoke($"[Direct:NVIDIA] Could not resolve exact NVIDIA PFID for '{gpu.DisplayName}' (series {psid}).");
            return candidates;
        }

        var installedNvVer = ConvertWindowsVersionToNvidiaVersion(gpu.DriverVersion);
        log?.Invoke($"[Direct:NVIDIA] Querying official NVIDIA {(isNotebook ? "Notebook" : "Desktop")} versions for {gpu.DisplayName} (psid={psid}, pfid={pfid}, installed={installedNvVer})...");

        // Query BOTH Game Ready (upCRD=0) and Studio (upCRD=1) channels for up to 8 versions total
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (upCrd, channelName) in new[] { (0, "game-ready"), (1, "studio") })
        {
            var url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                      $"?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID=57&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD={upCrd}&qnf=0&sort1=0&numberOfResults=8";

            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("IDS", out var ids)) continue;

            foreach (var item in ids.EnumerateArray())
            {
                if (!item.TryGetProperty("downloadInfo", out var info)) continue;
                if (info.TryGetProperty("Success", out var succ) && succ.GetString() != "1") continue;

                var ver = info.TryGetProperty("Version", out var vProp) ? vProp.GetString() ?? "" : "";
                var dlUrl = info.TryGetProperty("DownloadURL", out var uProp) ? uProp.GetString() ?? "" : "";
                var relDateStr = info.TryGetProperty("ReleaseDateTime", out var dProp) ? dProp.GetString() ?? "" : "";
                var sizeStr = info.TryGetProperty("DownloadURLFileSize", out var sProp) ? sProp.GetString() ?? "" : "";
                var name = info.TryGetProperty("Name", out var nProp) ? Uri.UnescapeDataString(nProp.GetString() ?? "NVIDIA GeForce Driver") : "NVIDIA GeForce Driver";

                if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(dlUrl)) continue;
                if (!seen.Add($"{ver}|{channelName}")) continue;

                DateTime? relDate = DateTime.TryParse(relDateStr, out var parsedDate) ? parsedDate : null;
                long sizeBytes = ParseHumanSize(sizeStr);
                bool isNewer = IsVersionNewer(ver, installedNvVer);

                var oemNote = (isNotebook && gpu.IsLaptopOemCustomized && preferOemOnLaptops)
                    ? $"[Laptop OEM Notice] Your laptop ({profile.NormalizedOem} {profile.SystemModel}) uses OEM-customized graphics. Verify {profile.NormalizedOem} support if you encounter MUX/brightness quirks.\n"
                    : "";

                candidates.Add(new DriverUpdateCandidate
                {
                    Id = $"nvidia:{pfid}:{channelName}:{ver}",
                    SourceKind = UpdateSourceKind.DirectManufacturer,
                    SourceName = "NVIDIA Official (Direct)",
                    Title = $"{name} {ver} ({(isNotebook ? "Notebook" : "Desktop")} DCH WHQL)",
                    Vendor = "NVIDIA",
                    Version = ver,
                    ReleaseDate = relDate,
                    SizeBytes = sizeBytes,
                    DownloadUrl = dlUrl,
                    InstallerType = "exe",
                    SilentArgs = "-s -noreboot",
                    Channel = channelName,
                    IsOemSpecific = false,
                    ReleaseNotes = $"{oemNote}Official NVIDIA {channelName.ToUpperInvariant()} WHQL DCH installer.\nInstalled: {installedNvVer} (Windows {gpu.DriverVersion}) | Selected: {ver}.",
                    MatchedHardwareId = gpu.HardwareId,
                    RebootRequired = false,
                    DeviceName = gpu.DisplayName,
                    DeviceId = gpu.DeviceId,
                    InstalledVersion = $"{installedNvVer} ({gpu.DriverVersion})",
                    IsNewerThanInstalled = isNewer,
                    MatchScore = isNewer ? ((isNotebook && preferOemOnLaptops) ? 78 : 95) : 55
                });
            }
        }

        if (candidates.Count > 0)
            log?.Invoke($"[Direct:NVIDIA] Loaded {candidates.Count} official version(s) for {gpu.DisplayName} (latest {candidates[0].Version}).");

        return candidates;
    }

    private static async Task<int> ResolveNvidiaPfidAsync(int psid, string gpuDisplayName, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3&ParentID={psid}";
            var xmlStr = await Http.GetStringAsync(url, ct);
            var xdoc = XDocument.Parse(xmlStr);

            var cleanTarget = Regex.Replace(gpuDisplayName, @"\b(NVIDIA|Laptop GPU)\b", "", RegexOptions.IgnoreCase).Trim();
            var values = xdoc.Descendants("LookupValue")
                .Select(el => (Name: el.Element("Name")?.Value?.Trim() ?? "", Val: int.TryParse(el.Element("Value")?.Value, out var v) ? v : 0))
                .Where(x => x.Val > 0 && !string.IsNullOrWhiteSpace(x.Name))
                .ToList();

            // Exact match first
            var exact = values.FirstOrDefault(x => x.Name.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase));
            if (exact.Val > 0) return exact.Val;

            // Substring match (longest matching model name wins, e.g., "RTX 3050 Ti" over "RTX 3050")
            var sub = values
                .Where(x => cleanTarget.Contains(x.Name, StringComparison.OrdinalIgnoreCase) ||
                            x.Name.Contains(cleanTarget, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Name.Length)
                .FirstOrDefault();
            if (sub.Val > 0) return sub.Val;

            return values.FirstOrDefault().Val;
        }
        catch
        {
            return 0;
        }
    }

    private static int DetectNvidiaSeriesId(string gpuName, bool isNotebook)
    {
        var n = gpuName.ToUpperInvariant();
        if (n.Contains("RTX 50")) return isNotebook ? 132 : 131;
        if (n.Contains("RTX 40")) return isNotebook ? 129 : 127;
        if (n.Contains("RTX 30")) return isNotebook ? 123 : 120;
        if (n.Contains("RTX 20")) return isNotebook ? 111 : 107;
        if (n.Contains("GTX 16")) return isNotebook ? 113 : 112;
        if (n.Contains("GTX 10")) return isNotebook ? 102 : 101;
        return isNotebook ? 129 : 127;
    }

    /// <summary>
    /// Converts Windows WDDM NVIDIA version (e.g. "32.0.16.2124") to NVIDIA marketing version ("621.24").
    /// </summary>
    public static string ConvertWindowsVersionToNvidiaVersion(string winVer)
    {
        if (string.IsNullOrWhiteSpace(winVer)) return "0.0";
        var parts = winVer.Split('.');
        if (parts.Length == 4 && parts[2].Length >= 1 && parts[3].Length == 4)
        {
            var lastDigitOf3 = parts[2][^1..];
            var major = lastDigitOf3 + parts[3][..2];
            var minor = parts[3][2..];
            return $"{major}.{minor}";
        }
        return winVer;
    }

    public static bool IsVersionNewer(string candidateVer, string installedVer)
    {
        if (string.IsNullOrWhiteSpace(installedVer) || installedVer.Equals("Missing", StringComparison.OrdinalIgnoreCase))
            return true;

        var cClean = CleanVersion(candidateVer);
        var iClean = CleanVersion(installedVer);
        if (Version.TryParse(cClean, out var cv) && Version.TryParse(iClean, out var iv))
            return cv > iv;

        return false;
    }

    public static Version ParseComparableVersion(string v)
    {
        var clean = CleanVersion(v);
        return Version.TryParse(clean, out var parsed) ? parsed : new Version(0, 0);
    }

    private static string CleanVersion(string v)
    {
        var m = Regex.Match(v ?? "", @"\d+(?:\.\d+){1,3}");
        return m.Success ? m.Value : "0.0";
    }

    private static long ParseHumanSize(string sizeStr)
    {
        if (string.IsNullOrWhiteSpace(sizeStr)) return 0;
        var m = Regex.Match(sizeStr, @"([\d\.]+)\s*(GB|MB|KB)", RegexOptions.IgnoreCase);
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
