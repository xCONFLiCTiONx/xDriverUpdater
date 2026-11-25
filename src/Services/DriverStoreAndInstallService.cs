using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using xDriverUpdater.Models;

namespace xDriverUpdater.Services;

/// <summary>
/// Enterprise-grade Windows Driver Store manager, backup/export utility,
/// System Restore Point creator, and safe package downloader/installer (pnputil / CAB / ZIP / EXE / MSI).
/// </summary>
public sealed class DriverStoreAndInstallService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    static DriverStoreAndInstallService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("xDriverUpdater/1.0 (Windows NT 10.0; Win64; x64)");
    }

    public Task<List<DriverStorePackage>> EnumerateDriverStoreAsync(List<DriverDevice> activeDevices, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var packages = new List<DriverStorePackage>();
            var (exit, output) = RunProcess("pnputil.exe", "/enum-drivers");
            if (exit != 0 && string.IsNullOrWhiteSpace(output))
                return packages;

            var activeInfs = new HashSet<string>(
                activeDevices.Select(d => d.InfName).Where(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

            DriverStorePackage? current = null;
            foreach (var rawLine in output.Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (current != null && !string.IsNullOrWhiteSpace(current.PublishedName))
                    {
                        packages.Add(current);
                        current = null;
                    }
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var val = line[(idx + 1)..].Trim();

                if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && !string.IsNullOrWhiteSpace(current.PublishedName))
                        packages.Add(current);
                    current = new DriverStorePackage { PublishedName = val };
                }
                else if (current != null)
                {
                    if (key.Equals("Original Name", StringComparison.OrdinalIgnoreCase))
                        current.OriginalName = val;
                    else if (key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase))
                        current.ProviderName = val;
                    else if (key.Equals("Class Name", StringComparison.OrdinalIgnoreCase))
                        current.ClassName = val;
                    else if (key.Equals("Driver Version", StringComparison.OrdinalIgnoreCase))
                    {
                        // Format: "08/14/2026 32.0.15.6614"
                        var parts = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (DateTime.TryParse(parts[0], out var dt)) current.DriverDate = dt;
                            current.DriverVersion = parts[1];
                        }
                        else
                        {
                            current.DriverVersion = val;
                        }
                    }
                    else if (key.Equals("Signer Name", StringComparison.OrdinalIgnoreCase))
                        current.SignerName = val;
                }
            }

            if (current != null && !string.IsNullOrWhiteSpace(current.PublishedName))
                packages.Add(current);

            // Mark active and superseded (stale) packages
            foreach (var p in packages)
                p.IsInUse = activeInfs.Contains(p.PublishedName);

            foreach (var group in packages.GroupBy(p => $"{p.OriginalName}|{p.ProviderName}", StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group
                    .OrderByDescending(p => p.IsInUse)
                    .ThenByDescending(p => p.DriverDate ?? DateTime.MinValue)
                    .ToList();

                for (int i = 1; i < ordered.Count; i++)
                {
                    if (!ordered[i].IsInUse)
                        ordered[i].IsSuperseded = true;
                }
            }

            return packages
                .OrderByDescending(p => p.IsSuperseded)
                .ThenBy(p => p.ClassName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    public Task<(bool Success, string Message)> DeleteDriverStorePackageAsync(string publishedInfName, bool force = false)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(publishedInfName) || !publishedInfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                return (false, "Only third-party OEM INF packages (oem*.inf) can be removed.");

            var args = force
                ? $"/delete-driver {publishedInfName} /uninstall /force"
                : $"/delete-driver {publishedInfName}";
            var (exit, output) = RunProcess("pnputil.exe", args);
            return (exit == 0, output.Trim());
        });
    }

    public async Task<(int RemovedCount, List<(DriverStorePackage Package, string Error)> Failed)> DeleteStalePackagesAsync(
        IReadOnlyList<DriverStorePackage> stalePackages,
        Action<string>? log = null,
        IProgress<(int Completed, int Total, DriverStorePackage Current)>? progress = null,
        CancellationToken ct = default)
    {
        int removed = 0;
        var failed = new List<(DriverStorePackage Package, string Error)>();

        for (int i = 0; i < stalePackages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pkg = stalePackages[i];
            progress?.Report((i, stalePackages.Count, pkg));

            if (pkg.IsInUse || !pkg.IsSuperseded)
            {
                failed.Add((pkg, "Skipped because package is not marked stale/inactive."));
                log?.Invoke($"[DriverStore] Skipped {pkg.PublishedName} ({pkg.OriginalName}): package is not stale/inactive.");
                continue;
            }

            var (ok, msg) = await DeleteDriverStorePackageAsync(pkg.PublishedName, force: false);
            var summary = string.IsNullOrWhiteSpace(msg)
                ? (ok ? "Deleted." : "pnputil failed.")
                : msg.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? msg;

            if (ok)
            {
                removed++;
                log?.Invoke($"[DriverStore] Deleted stale package {pkg.PublishedName} ({pkg.OriginalName}, v{pkg.DriverVersion}): {summary}");
            }
            else
            {
                failed.Add((pkg, summary));
                log?.Invoke($"[DriverStore] Failed to delete {pkg.PublishedName} ({pkg.OriginalName}): {summary}");
            }

            progress?.Report((i + 1, stalePackages.Count, pkg));
        }

        return (removed, failed);
    }

    public Task<(bool Success, string OutputFolder, string Message)> ExportAllDriversBackupAsync(
        string baseBackupDir,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var target = Path.Combine(baseBackupDir, $"FullBackup-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(target);
            log?.Invoke($"[Backup] Exporting all third-party Windows drivers to {target}...");
            var (exit, output) = RunProcess("pnputil.exe", $"/export-driver * \"{target}\"");
            log?.Invoke($"[Backup] pnputil exited with code {exit}.");
            return (exit == 0, target, exit == 0 ? $"Backed up third-party drivers to {target}" : output.Trim());
        }, ct);
    }

    public Task<(bool Success, string Message)> ExportSingleDriverBackupAsync(string publishedInfName, string baseBackupDir, Action<string>? log = null)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(publishedInfName) || !publishedInfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                return (true, "Inbox driver does not require OEM INF export.");

            var target = Path.Combine(baseBackupDir, $"Single-{Path.GetFileNameWithoutExtension(publishedInfName)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(target);
            log?.Invoke($"[Backup] Exporting {publishedInfName} -> {target}");
            var (exit, output) = RunProcess("pnputil.exe", $"/export-driver {publishedInfName} \"{target}\"");
            return (exit == 0, exit == 0 ? $"Exported {publishedInfName} to {target}" : output.Trim());
        });
    }

    public Task<(bool Success, string Message)> CreateSystemRestorePointAsync(string description, Action<string>? log = null)
    {
        return Task.Run(() =>
        {
            log?.Invoke($"[Safety] Creating Windows System Restore Point: '{description}'...");
            var cmd = $"-NoProfile -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description '{description.Replace("'", "")}' -RestorePointType MODIFY_SETTINGS\"";
            var (exit, output) = RunProcess("powershell.exe", cmd);
            if (exit == 0)
            {
                log?.Invoke("[Safety] System Restore Point created.");
                return (true, "System Restore Point created.");
            }

            // Windows enforces a 24-hour cooldown between restore points by default; treat gracefully
            log?.Invoke($"[Safety] Restore point skipped or cooldown active: {output.Trim()}");
            return (false, output.Trim());
        });
    }

    public async Task<(bool Success, bool RebootRequired, string Message)> InstallCandidateAsync(
        DriverUpdateCandidate candidate,
        DriverDevice? device,
        AppSettings settings,
        WindowsUpdateDriverService wuaService,
        Action<string>? log = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        bool silentMode = false)
    {
        // Browser-only portal link
        if (candidate.InstallerType.Equals("page", StringComparison.OrdinalIgnoreCase))
        {
            OpenUrl(candidate.DownloadUrl);
            log?.Invoke($"[Open] Opened official vendor download page: {candidate.DownloadUrl}");
            return (true, false, "Opened official vendor download page in your browser.");
        }

        // Pre-install safety checks
        if (settings.BackupDriverBeforeInstall && device != null && !string.IsNullOrWhiteSpace(device.InfName))
        {
            await ExportSingleDriverBackupAsync(device.InfName, settings.BackupDirectory, log);
        }

        if (candidate.SourceKind == UpdateSourceKind.WindowsUpdate || candidate.InstallerType == "wua")
        {
            return await wuaService.DownloadAndInstallWuaUpdatesAsync(new List<DriverUpdateCandidate> { candidate }, log, ct);
        }

        if (candidate.InstallerType.Equals("local-inf", StringComparison.OrdinalIgnoreCase))
        {
            return await InstallLocalPackageAsync(candidate.DownloadUrl, log, ct);
        }

        // Resolve Microsoft Update Catalog direct .cab URL on demand
        var effectiveUrl = candidate.DownloadUrl;
        var effectiveType = candidate.InstallerType;
        if (candidate.InstallerType.Equals("ms-catalog-cab", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke($"[MS Catalog] Resolving direct WHQL .cab download URL for {candidate.CatalogUpdateGuid}...");
            var catalogSvc = new MicrosoftCatalogService();
            var cabUrl = await catalogSvc.ResolveCabDownloadUrlAsync(candidate.CatalogUpdateGuid, ct);
            if (string.IsNullOrWhiteSpace(cabUrl))
                return (false, false, "Could not resolve direct .cab URL from Microsoft Update Catalog. Use 'Open Download / Portal' to grab it manually.");
            effectiveUrl = cabUrl;
            effectiveType = "cab";
        }

        // Download from URL
        if (string.IsNullOrWhiteSpace(effectiveUrl))
            return (false, false, "No download URL provided for this package.");

        Directory.CreateDirectory(settings.DownloadCacheDirectory);
        var fileName = InferFileName(effectiveUrl, effectiveType);
        var localPath = Path.Combine(settings.DownloadCacheDirectory, $"{SanitizeFileName(candidate.Vendor)}-{SanitizeFileName(candidate.Version)}-{fileName}");

        log?.Invoke($"[Download] Downloading {candidate.Title} (v{candidate.Version}) from {effectiveUrl}...");
        await DownloadFileWithProgressAsync(effectiveUrl, localPath, progress, ct);
        log?.Invoke($"[Download] Saved to {localPath}");

        // Optional SHA-256 verification
        if (!string.IsNullOrWhiteSpace(candidate.Sha256))
        {
            log?.Invoke("[Verify] Verifying SHA-256 checksum...");
            var actual = await ComputeSha256Async(localPath, ct);
            if (!actual.Equals(candidate.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(localPath);
                var err = $"SHA-256 mismatch! Expected {candidate.Sha256}, got {actual}. File deleted for safety.";
                log?.Invoke($"[Verify] {err}");
                return (false, false, err);
            }
            log?.Invoke("[Verify] SHA-256 verified OK.");
        }

        // Install by type
        if (effectiveType is "inf-zip" or "cab")
        {
            return await InstallLocalPackageAsync(localPath, log, ct);
        }

        if (effectiveType is "exe" or "msi")
        {
            log?.Invoke($"[Install] Launching vendor installer: {localPath} {candidate.SilentArgs}");
            var psi = effectiveType == "msi"
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{localPath}\" {candidate.SilentArgs}") { UseShellExecute = true }
                : new ProcessStartInfo(localPath, candidate.SilentArgs) { UseShellExecute = true };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, false, "Failed to start vendor installer.");
            await proc.WaitForExitAsync(ct);
            bool ok = proc.ExitCode is 0 or 3010;
            bool reboot = proc.ExitCode == 3010;
            log?.Invoke($"[Install] Vendor installer exited with code {proc.ExitCode}.");
            return (ok, reboot, ok ? "Vendor installer completed." : $"Installer exited with code {proc.ExitCode}.");
        }

        return (false, false, $"Unsupported installer type: {effectiveType}");
    }

    public Task<(bool Success, bool RebootRequired, string Message)> InstallLocalPackageAsync(
        string fileOrFolderPath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (Directory.Exists(fileOrFolderPath))
            {
                log?.Invoke($"[PnPUtil] Staging & installing INF drivers in folder: {fileOrFolderPath}");
                var (exit, output) = RunProcess("pnputil.exe", $"/add-driver \"{Path.Combine(fileOrFolderPath, "*.inf")}\" /subdirs /install");
                log?.Invoke(output.Trim());
                return (exit is 0 or 3010, exit == 3010, output.Trim());
            }

            if (!File.Exists(fileOrFolderPath))
                return (false, false, $"File not found: {fileOrFolderPath}");

            var ext = Path.GetExtension(fileOrFolderPath).ToLowerInvariant();
            if (ext == ".inf")
            {
                log?.Invoke($"[PnPUtil] Installing INF: {fileOrFolderPath}");
                var (exit, output) = RunProcess("pnputil.exe", $"/add-driver \"{fileOrFolderPath}\" /install");
                log?.Invoke(output.Trim());
                return (exit is 0 or 3010, exit == 3010, output.Trim());
            }

            if (ext is ".zip" or ".cab")
            {
                var extractDir = Path.Combine(Path.GetTempPath(), "xDriverUpdater-Extract", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(extractDir);
                try
                {
                    if (ext == ".zip")
                    {
                        log?.Invoke($"[Extract] Extracting ZIP archive to {extractDir}...");
                        ZipFile.ExtractToDirectory(fileOrFolderPath, extractDir, overwriteFiles: true);
                    }
                    else
                    {
                        log?.Invoke($"[Extract] Expanding CAB package to {extractDir}...");
                        var (expExit, expOut) = RunProcess("expand.exe", $"\"{fileOrFolderPath}\" -F:* \"{extractDir}\"");
                        if (expExit != 0)
                            return (false, false, $"CAB expansion failed: {expOut.Trim()}");
                    }

                    log?.Invoke($"[PnPUtil] Installing extracted INF driver(s)...");
                    var (exit, output) = RunProcess("pnputil.exe", $"/add-driver \"{Path.Combine(extractDir, "*.inf")}\" /subdirs /install");
                    log?.Invoke(output.Trim());
                    return (exit is 0 or 3010, exit == 3010, output.Trim());
                }
                finally
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }
            }

            return (false, false, $"Unsupported file extension: {ext}. Expected .inf, .cab, .zip, or a driver folder.");
        }, ct);
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static async Task DownloadFileWithProgressAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0)
                progress?.Report((double)readTotal / total * 100.0);
        }
        progress?.Report(100.0);
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(filePath);
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string InferFileName(string url, string installerType)
    {
        try
        {
            var uri = new Uri(url);
            var seg = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(seg) && seg.Contains('.'))
                return SanitizeFileName(seg);
        }
        catch { }

        return installerType switch
        {
            "cab" => "driver.cab",
            "exe" => "installer.exe",
            "msi" => "installer.msi",
            _ => "driver.zip"
        };
    }

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "pkg";
        return Regex.Replace(s, @"[^A-Za-z0-9._\-]", "_");
    }

    public static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return (-1, "Failed to start process.");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
            return (p.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
