using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Manages pluggable driver repositories (built-in catalog, remote JSON URLs on GitHub/R2/xServer,
/// local JSON manifests, and local driver folders) and matches packages to system hardware IDs.
/// </summary>
public sealed class RepositoryManagerService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static RepositoryManagerService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("xDriverUpdater/1.0");
    }

    public async Task<List<DriverUpdateCandidate>> ScanRepositoriesAsync(
        SystemProfile profile,
        List<DriverDevice> devices,
        List<RepositorySourceConfig> repositories,
        bool preferOemOnLaptops,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        var matches = new List<DriverUpdateCandidate>();

        foreach (var repo in repositories.Where(r => r.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var manifest = await LoadManifestAsync(repo, ct);
                if (manifest == null)
                {
                    repo.LastStatus = "Failed to load manifest";
                    continue;
                }

                repo.LastSyncedUtc = DateTime.UtcNow;
                repo.PackageCount = manifest.Packages.Count;
                repo.LastStatus = $"Synced ({manifest.Packages.Count} pkgs)";
                log?.Invoke($"[Repo] Loaded '{repo.Name}': {manifest.Packages.Count} package(s).");

                foreach (var pkg in manifest.Packages)
                {
                    if (!MatchesFormFactor(pkg.FormFactor, profile.IsLaptop))
                        continue;

                    foreach (var dev in devices)
                    {
                        var matchedHwid = FindMatchingHardwareId(dev, pkg.HardwareIds);
                        if (matchedHwid == null) continue;

                        // Check OEM vendor restriction if package is OEM-specific
                        bool pkgIsOem = !string.IsNullOrWhiteSpace(pkg.OemVendor);
                        if (pkgIsOem)
                        {
                            var devOem = !string.IsNullOrEmpty(dev.SubsystemOemName) ? dev.SubsystemOemName : profile.NormalizedOem;
                            if (!devOem.Equals(pkg.OemVendor, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        // Only include real versioned downloadable/installable packages in Updates Available
                        // (skip unversioned "page" links so we never show fake "Latest" rows)
                        if (string.IsNullOrWhiteSpace(pkg.Version) ||
                            pkg.InstallerType.Equals("page", StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool isNewer = DirectManufacturerService.IsVersionNewer(pkg.Version, dev.DriverVersion);

                        // Compute match score: OEM-exact on laptop scores 100, exact HWID scores 90, wildcard scores 82
                        int score = isNewer ? 82 : 50;
                        if (!matchedHwid.Contains('*')) score += 8;
                        if (pkgIsOem && profile.IsLaptop) score += 10;
                        if (!pkgIsOem && dev.IsLaptopOemCustomized && preferOemOnLaptops) score -= 12;

                        DateTime? relDate = DateTime.TryParse(pkg.ReleaseDate, out var dt) ? dt : null;

                        var cand = new DriverUpdateCandidate
                        {
                            Id = $"{repo.Id}:{pkg.Id}:{dev.DeviceId.GetHashCode():X}",
                            SourceKind = UpdateSourceKind.CustomRepository,
                            SourceName = repo.Name,
                            Title = pkg.Name,
                            Vendor = pkgIsOem ? $"{pkg.OemVendor} ({pkg.Vendor})" : pkg.Vendor,
                            Version = pkg.Version,
                            ReleaseDate = relDate,
                            SizeBytes = pkg.SizeBytes,
                            DownloadUrl = pkg.DownloadUrl,
                            Sha256 = pkg.Sha256,
                            InstallerType = string.IsNullOrWhiteSpace(pkg.InstallerType) ? "inf-zip" : pkg.InstallerType,
                            SilentArgs = pkg.SilentArgs,
                            Channel = string.IsNullOrWhiteSpace(pkg.Channel) ? "stable" : pkg.Channel,
                            IsOemSpecific = pkgIsOem,
                            ReleaseNotes = pkg.ReleaseNotes,
                            MatchedHardwareId = matchedHwid,
                            DeviceName = dev.DisplayName,
                            DeviceId = dev.DeviceId,
                            InstalledVersion = dev.DriverVersion,
                            IsNewerThanInstalled = isNewer,
                            MatchScore = score
                        };

                        // Avoid duplicate candidate for same device + version + source
                        if (!matches.Any(m => m.DeviceId == cand.DeviceId && m.Version == cand.Version && m.SourceName == cand.SourceName))
                        {
                            matches.Add(cand);
                            dev.AvailableUpdates.Add(cand);
                            if (isNewer && !dev.HasProblem)
                                dev.HealthStatus = DriverHealthStatus.UpdateAvailable;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                repo.LastStatus = $"Error: {ex.Message}";
                log?.Invoke($"[Repo] Error loading '{repo.Name}': {ex.Message}");
            }
        }

        return matches;
    }

    public async Task<RepositoryManifest?> LoadManifestAsync(RepositorySourceConfig repo, CancellationToken ct = default)
    {
        if (repo.IsBuiltIn || repo.UrlOrPath.Equals("builtin://core", StringComparison.OrdinalIgnoreCase))
            return BuildCoreCatalog();

        var src = repo.UrlOrPath.Trim();
        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var json = await Http.GetStringAsync(src, ct);
            return JsonSerializer.Deserialize<RepositoryManifest>(json, JsonOpts);
        }

        if (File.Exists(src))
        {
            var json = await File.ReadAllTextAsync(src, ct);
            return JsonSerializer.Deserialize<RepositoryManifest>(json, JsonOpts);
        }

        if (Directory.Exists(src))
        {
            var manifestPath = Path.Combine(src, "manifest.json");
            if (File.Exists(manifestPath))
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                return JsonSerializer.Deserialize<RepositoryManifest>(json, JsonOpts);
            }

            // Auto-index a local driver folder containing .inf / .cab / .zip files!
            return BuildLocalFolderManifest(repo.Name, src);
        }

        throw new FileNotFoundException($"Repository path or URL not found: {src}");
    }

    private static RepositoryManifest BuildLocalFolderManifest(string name, string folderPath)
    {
        var manifest = new RepositoryManifest
        {
            Name = name,
            Maintainer = "Local Folder",
            Description = $"Auto-indexed driver folder: {folderPath}",
            UpdatedUtc = DateTime.UtcNow.ToString("O")
        };

        // Scan .inf files for Hardware IDs (simple fast parser for PCI\VEN_... / USB\VID_...)
        var infs = Directory.EnumerateFiles(folderPath, "*.inf", SearchOption.AllDirectories).Take(200);
        foreach (var inf in infs)
        {
            try
            {
                var content = File.ReadAllText(inf);
                var hwMatches = Regex.Matches(content, @"(?:PCI|USB|HDAUDIO)\\(?:VEN|VID)_[0-9A-Fa-f]{4}&(?:DEV|PID)_[0-9A-Fa-f]{4}(?:&SUBSYS_[0-9A-Fa-f]{8})?", RegexOptions.IgnoreCase)
                    .Select(m => m.Value.ToUpperInvariant())
                    .Distinct()
                    .Take(50)
                    .ToList();

                if (hwMatches.Count == 0) continue;

                var verMatch = Regex.Match(content, @"DriverVer\s*=\s*([0-9/\-]+)\s*,\s*([0-9\.]+)", RegexOptions.IgnoreCase);
                var ver = verMatch.Success ? verMatch.Groups[2].Value : "";
                var date = verMatch.Success ? verMatch.Groups[1].Value : "";

                manifest.Packages.Add(new RepoDriverPackage
                {
                    Id = Path.GetFileNameWithoutExtension(inf),
                    Name = $"{Path.GetFileName(Path.GetDirectoryName(inf))} ({Path.GetFileName(inf)})",
                    Vendor = "Local INF Repository",
                    Version = ver,
                    ReleaseDate = date,
                    HardwareIds = hwMatches,
                    DownloadUrl = inf,
                    InstallerType = "local-inf",
                    Channel = "local",
                    ReleaseNotes = $"Local INF package from {inf}"
                });
            }
            catch { }
        }

        return manifest;
    }

    private static RepositoryManifest BuildCoreCatalog()
    {
        // Curated baseline catalog demonstrating OEM-aware and vendor-aware matching.
        // Users can add unlimited external JSON repos (GitHub, R2, xServer, NAS) to extend this!
        return new RepositoryManifest
        {
            SchemaVersion = 1,
            Name = "xDriverUpdater Core Vendor Catalog",
            Maintainer = "xPrograms",
            Description = "Built-in baseline vendor & OEM reference catalog.",
            UpdatedUtc = "2026-11-24T00:00:00Z",
            Packages = new List<RepoDriverPackage>
            {
                new()
                {
                    Id = "amd-chipset-am4-am5",
                    Name = "AMD Ryzen Chipset Software (AM4 / AM5)",
                    Vendor = "AMD",
                    FormFactor = "any",
                    Category = "Chipset & System",
                    HardwareIds = new() { "PCI\\VEN_1022&DEV_148*", "PCI\\VEN_1022&DEV_163*", "PCI\\VEN_1022&DEV_14E*", "PCI\\VEN_1022&DEV_790B*" },
                    Version = "",
                    DownloadUrl = "https://www.amd.com/en/support/download/drivers.html",
                    InstallerType = "page",
                    Channel = "whql",
                    ReleaseNotes = "Official AMD Chipset Drivers (PCI, SMBus, GPIO, PSP, PPM/CPPC2)."
                },
                new()
                {
                    Id = "focusrite-control-usb",
                    Name = "Focusrite Scarlett / Clarett USB Audio Driver",
                    Vendor = "Focusrite",
                    FormFactor = "any",
                    Category = "Audio",
                    HardwareIds = new() { "USB\\VID_1235&PID_*" },
                    Version = "",
                    DownloadUrl = "https://downloads.focusrite.com/focusrite",
                    InstallerType = "page",
                    Channel = "stable",
                    ReleaseNotes = "Official Focusrite low-latency ASIO & WDM USB audio driver."
                },
                new()
                {
                    Id = "realtek-pcie-gbe",
                    Name = "Realtek PCIe GbE / 2.5GbE Family Controller",
                    Vendor = "Realtek",
                    FormFactor = "any",
                    Category = "Network",
                    HardwareIds = new() { "PCI\\VEN_10EC&DEV_8168*", "PCI\\VEN_10EC&DEV_8125*", "PCI\\VEN_10EC&DEV_8126*" },
                    Version = "",
                    DownloadUrl = "https://www.realtek.com/Download/List?cate_id=584",
                    InstallerType = "page",
                    Channel = "whql",
                    ReleaseNotes = "Official Realtek PCIe Ethernet NDIS / NetAdapterCx driver."
                },
                new()
                {
                    Id = "intel-wifi-bt",
                    Name = "Intel Wireless Wi-Fi & Bluetooth Driver Suite",
                    Vendor = "Intel",
                    FormFactor = "any",
                    Category = "Network",
                    HardwareIds = new() { "PCI\\VEN_8086&DEV_2723*", "PCI\\VEN_8086&DEV_2725*", "PCI\\VEN_8086&DEV_272B*", "PCI\\VEN_8086&DEV_51F0*" },
                    Version = "",
                    DownloadUrl = "https://www.intel.com/content/www/us/en/download/19351/intel-wireless-wi-fi-drivers-for-windows-10-and-windows-11.html",
                    InstallerType = "page",
                    Channel = "whql",
                    ReleaseNotes = "Official Intel AX200/AX210/AX211/BE200 Wi-Fi driver."
                },
                new()
                {
                    Id = "acer-laptop-intel-gpu-oem",
                    Name = "Acer OEM Customized Intel Graphics Driver Portal",
                    Vendor = "Intel",
                    OemVendor = "Acer",
                    FormFactor = "laptop",
                    Category = "Display",
                    HardwareIds = new() { "PCI\\VEN_8086&DEV_*&SUBSYS_*1025*" },
                    Version = "",
                    DownloadUrl = "https://www.acer.com/us-en/support/drivers-and-manuals",
                    InstallerType = "page",
                    Channel = "oem",
                    ReleaseNotes = "Acer OEM-validated Intel Graphics driver preserving Acer laptop power, backlight, and hybrid GPU tuning."
                }
            }
        };
    }

    public static string GenerateSampleManifestJson()
    {
        var sample = new RepositoryManifest
        {
            SchemaVersion = 1,
            Name = "My Custom Driver Repository",
            Maintainer = "Your Name",
            Description = "Custom JSON driver repository hosted on GitHub, Cloudflare R2, xServer, or a local folder.",
            UpdatedUtc = DateTime.UtcNow.ToString("O"),
            Packages = new List<RepoDriverPackage>
            {
                new()
                {
                    Id = "acer-nitro-intel-gpu",
                    Name = "Acer OEM Intel Iris Xe Graphics (Validated)",
                    Vendor = "Intel",
                    OemVendor = "Acer",
                    FormFactor = "laptop",
                    Category = "Display",
                    HardwareIds = new() { "PCI\\VEN_8086&DEV_*&SUBSYS_*1025*" },
                    Version = "32.0.101.6314",
                    ReleaseDate = "2026-10-15",
                    SizeBytes = 524288000,
                    DownloadUrl = "https://example.com/drivers/acer-intel-gpu-32.0.101.6314.zip",
                    Sha256 = "",
                    InstallerType = "inf-zip",
                    SilentArgs = "",
                    Channel = "oem",
                    ReleaseNotes = "OEM-validated Intel graphics package for Acer laptops (PCI SVID 1025)."
                },
                new()
                {
                    Id = "realtek-25gbe-whql",
                    Name = "Realtek PCIe 2.5GbE NetAdapterCx WHQL",
                    Vendor = "Realtek",
                    FormFactor = "any",
                    Category = "Network",
                    HardwareIds = new() { "PCI\\VEN_10EC&DEV_8125*" },
                    Version = "1125.21.903.2026",
                    ReleaseDate = "2026-09-03",
                    SizeBytes = 6400000,
                    DownloadUrl = "https://example.com/drivers/realtek-8125-whql.cab",
                    Sha256 = "",
                    InstallerType = "cab",
                    Channel = "whql",
                    ReleaseNotes = "Signed CAB package installed directly via pnputil."
                }
            }
        };
        return JsonSerializer.Serialize(sample, JsonOpts);
    }

    private static bool MatchesFormFactor(string pkgFormFactor, bool isLaptop)
    {
        if (string.IsNullOrWhiteSpace(pkgFormFactor) || pkgFormFactor.Equals("any", StringComparison.OrdinalIgnoreCase))
            return true;
        if (pkgFormFactor.Equals("laptop", StringComparison.OrdinalIgnoreCase))
            return isLaptop;
        if (pkgFormFactor.Equals("desktop", StringComparison.OrdinalIgnoreCase))
            return !isLaptop;
        return true;
    }

    private static string? FindMatchingHardwareId(DriverDevice dev, List<string> patterns)
    {
        if (patterns == null || patterns.Count == 0) return null;
        var deviceIds = dev.HardwareIds.Concat(dev.CompatibleIds).Append(dev.HardwareId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            foreach (var id in deviceIds)
            {
                if (Regex.IsMatch(id, regex, RegexOptions.IgnoreCase))
                    return pattern;
            }
        }
        return null;
    }
}
